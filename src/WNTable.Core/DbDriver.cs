using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using WNTable.Core.Drivers;

namespace WNTable.Core;

public abstract class DbDriver
{
    protected DbDriver(ConnectionInfo info)
    {
        Info = info;
        CurrentDatabase = info.Database;
    }

    public ConnectionInfo Info { get; }
    public abstract DbKind Kind { get; }

    /// <summary>Database the workspace is browsing. Empty means "none selected yet".</summary>
    public string CurrentDatabase { get; set; }

    /// <summary>Schema hidden from table names (public, dbo). Null when the engine has no schemas.</summary>
    public virtual string? DefaultSchema => null;

    /// <summary>Whether the sidebar can switch databases (SQLite cannot).</summary>
    public virtual bool SupportsDatabases => true;

    public static DbDriver Create(ConnectionInfo info) => info.Kind switch
    {
        DbKind.MySql => new MySqlDriver(info),
        DbKind.PostgreSql => new PostgresDriver(info),
        DbKind.Sqlite => new SqliteDriver(info),
        DbKind.SqlServer => new SqlServerDriver(info),
        _ => throw new NotSupportedException(info.Kind.ToString()),
    };

    protected abstract DbConnection CreateConnection();

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = CreateConnection();
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task TestAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
    }

    public abstract string Quote(string identifier);

    public string Qualify(TableRef t) => t.Schema is null ? Quote(t.Name) : Quote(t.Schema) + "." + Quote(t.Name);

    protected TableRef MakeTable(string? schema, string name, bool isView)
    {
        var display = schema is null || string.Equals(schema, DefaultSchema, StringComparison.OrdinalIgnoreCase)
            ? name
            : schema + "." + name;
        return new TableRef(schema, name, isView, display);
    }

    public abstract Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<TableRef>> GetTablesAsync(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken ct = default);

    public virtual async Task<string> GetCreateStatementAsync(TableRef table, CancellationToken ct = default)
    {
        var cols = await GetColumnsAsync(table, ct);
        var sb = new StringBuilder($"CREATE TABLE {Qualify(table)} (\n");
        var lines = cols.Select(c =>
            $"  {Quote(c.Name)} {c.DataType}{(c.Nullable ? "" : " NOT NULL")}{(c.Default is null ? "" : " DEFAULT " + c.Default)}").ToList();
        var pk = cols.Where(c => c.IsPrimaryKey).Select(c => Quote(c.Name)).ToList();
        if (pk.Count > 0) lines.Add($"  PRIMARY KEY ({string.Join(", ", pk)})");
        sb.Append(string.Join(",\n", lines)).Append("\n);");
        return sb.ToString();
    }

    /// <summary>table display name → column names, for autocomplete.</summary>
    public virtual async Task<Dictionary<string, List<string>>> GetAllColumnNamesAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in await GetTablesAsync(ct))
        {
            map[t.Display] = (await GetColumnsAsync(t, ct)).Select(c => c.Name).ToList();
        }
        return map;
    }

    public virtual string BuildSelect(TableRef table, string? where, string? orderBy, int limit, int offset)
    {
        var sb = new StringBuilder($"SELECT * FROM {Qualify(table)}");
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where);
        if (!string.IsNullOrWhiteSpace(orderBy)) sb.Append(" ORDER BY ").Append(orderBy);
        sb.Append(" LIMIT ").Append(limit).Append(" OFFSET ").Append(offset);
        return sb.ToString();
    }

    public string BuildCount(TableRef table, string? where)
    {
        var sql = $"SELECT COUNT(*) FROM {Qualify(table)}";
        return string.IsNullOrWhiteSpace(where) ? sql : sql + " WHERE " + where;
    }

    /// <summary>Placeholder for a value written to <paramref name="column"/>. PostgreSQL adds a cast.</summary>
    public virtual string Placeholder(string name, ColumnInfo? column) => "@" + name;

    // ---------- shared helpers ----------

    protected async Task<List<object?[]>> QueryRowsAsync(string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var conn = await OpenAsync(ct);
        return await QueryRowsAsync(conn, sql, ct, args);
    }

    protected static async Task<List<object?[]>> QueryRowsAsync(DbConnection conn, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) AddParameter(cmd, name, value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    protected static string? Str(object? v) => v switch
    {
        null or DBNull => null,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    protected static bool Bool(object? v) => v switch
    {
        null or DBNull => false,
        bool b => b,
        string s => s is "1" or "YES" or "yes" or "true" or "t",
        _ => Convert.ToInt64(v) != 0,
    };

    public static void AddParameter(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = "@" + name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    // ---------- running queries ----------

    /// <summary>Reads every result set of <paramref name="reader"/> into DataTables (columns c0..cN, real name in Caption).</summary>
    public static async Task ReadResultsAsync(DbDataReader reader, ExecuteResult result, int maxRows, CancellationToken ct)
    {
        do
        {
            if (reader.FieldCount == 0) continue;
            result.Tables.Add(await ReadTableAsync(reader, maxRows, result, ct));
        } while (await reader.NextResultAsync(ct));
    }

    private static async Task<DataTable> ReadTableAsync(DbDataReader reader, int maxRows, ExecuteResult result, CancellationToken ct)
    {
        var table = new DataTable();
        var fieldCount = reader.FieldCount;
        for (var i = 0; i < fieldCount; i++)
        {
            // Every column is object-typed: grid edits come back as strings and are sent as-is.
            var col = table.Columns.Add("c" + i, typeof(object));
            col.Caption = reader.GetName(i);
            Type clr;
            try { clr = reader.GetFieldType(i); } catch { clr = typeof(object); }
            col.ExtendedProperties["ClrType"] = clr;
            try { col.ExtendedProperties["DbType"] = reader.GetDataTypeName(i); } catch { /* informational only */ }
        }

        table.BeginLoadData();
        var values = new object[fieldCount];
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            if (count >= maxRows)
            {
                result.Truncated = true;
                break;
            }
            for (var i = 0; i < fieldCount; i++)
            {
                values[i] = ReadValue(reader, i);
            }
            table.LoadDataRow(values, true);
            count++;
        }
        table.EndLoadData();
        return table;
    }

    private static object ReadValue(DbDataReader reader, int i)
    {
        try
        {
            return reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
        }
        catch
        {
            // Types the provider cannot map (exotic PG types, invalid dates): fall back to text.
            try { return reader.GetString(i); }
            catch (Exception ex) { return $"<{ex.GetType().Name}>"; }
        }
    }

    public async Task<ExecuteResult> ExecuteAsync(DbConnection conn, IReadOnlyList<string> statements, int maxRows, CancellationToken ct)
    {
        var result = new ExecuteResult();
        var sw = Stopwatch.StartNew();
        foreach (var sql in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            var before = result.Tables.Count;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                await ReadResultsAsync(reader, result, maxRows, ct);
                await reader.CloseAsync();
                if (reader.RecordsAffected > 0) result.RecordsAffected += reader.RecordsAffected;
                if (result.Tables.Count == before)
                {
                    result.Messages.Add($"{Shorten(sql)} — {Math.Max(reader.RecordsAffected, 0)} dòng bị ảnh hưởng");
                }
            }
        }
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private static string Shorten(string sql)
    {
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length > 80 ? oneLine[..80] + "…" : oneLine;
    }

    /// <summary>Runs generated change statements in one transaction; every statement must touch exactly one row.</summary>
    public async Task ApplyChangesAsync(IReadOnlyList<SqlStatement> statements, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var st in statements)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = st.Sql;
            foreach (var (name, value) in st.Parameters) AddParameter(cmd, name, value);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            if (affected == 0)
            {
                throw new InvalidOperationException(
                    "Không tìm thấy dòng cần cập nhật (có thể đã bị thay đổi ở nơi khác). Đã hoàn tác toàn bộ.\n\n" + st.Preview);
            }
        }
        await tx.CommitAsync(ct);
    }
}
