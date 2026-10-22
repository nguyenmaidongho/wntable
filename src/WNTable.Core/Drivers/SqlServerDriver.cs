using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;

namespace WNTable.Core.Drivers;

/// <remarks>Not tested against a live server yet.</remarks>
public sealed class SqlServerDriver : DbDriver
{
    public SqlServerDriver(ConnectionInfo info) : base(info)
    {
        if (string.IsNullOrEmpty(CurrentDatabase)) CurrentDatabase = "master";
    }

    public override DbKind Kind => DbKind.SqlServer;
    public override string? DefaultSchema => "dbo";

    protected override DbConnection CreateConnection()
    {
        var port = Info.Port > 0 ? Info.Port : 1433;
        var b = new SqlConnectionStringBuilder
        {
            // A host like "PC\SQLEXPRESS" names an instance; the port is resolved by SQL Browser.
            DataSource = Info.Host.Contains('\\') ? Info.Host : $"{Info.Host},{port}",
            InitialCatalog = CurrentDatabase,
            TrustServerCertificate = Info.TrustServerCertificate,
            ConnectTimeout = 10,
            ApplicationName = "WNTable",
        };
        if (string.IsNullOrEmpty(Info.User))
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = Info.User;
            b.Password = Info.Password;
        }
        return new SqlConnection(b.ConnectionString);
    }

    public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public override string BuildSelect(TableRef table, string? where, string? orderBy, int limit, int offset)
    {
        var sb = new StringBuilder($"SELECT * FROM {Qualify(table)}");
        if (!string.IsNullOrWhiteSpace(where)) sb.Append(" WHERE ").Append(where);
        sb.Append(" ORDER BY ").Append(string.IsNullOrWhiteSpace(orderBy) ? "(SELECT NULL)" : orderBy);
        sb.Append($" OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY");
        return sb.ToString();
    }

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync("SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name", ct);
        return rows.Select(r => Str(r[0])!).ToList();
    }

    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY CASE WHEN TABLE_SCHEMA = 'dbo' THEN 0 ELSE 1 END, TABLE_SCHEMA, TABLE_NAME", ct);
        return rows.Select(r => MakeTable(Str(r[0]), Str(r[1])!, Str(r[2]) == "VIEW")).ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            """
            SELECT c.name,
                   TYPE_NAME(c.user_type_id) + CASE
                       WHEN TYPE_NAME(c.user_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
                           THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
                       WHEN TYPE_NAME(c.user_type_id) IN ('nvarchar', 'nchar')
                           THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                       WHEN TYPE_NAME(c.user_type_id) IN ('decimal', 'numeric')
                           THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                       ELSE '' END,
                   c.is_nullable,
                   OBJECT_DEFINITION(c.default_object_id),
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM sys.index_columns ic
                       JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                   THEN 1 ELSE 0 END AS bit),
                   CASE WHEN c.is_identity = 1 THEN 'identity' WHEN c.is_computed = 1 THEN 'computed' ELSE '' END,
                   CAST(ep.value AS nvarchar(max))
            FROM sys.columns c
            LEFT JOIN sys.extended_properties ep ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
            WHERE c.object_id = OBJECT_ID(@obj)
            ORDER BY c.column_id
            """, ct, ("obj", Qualify(table)));
        return rows.Select(r => new ColumnInfo
        {
            Name = Str(r[0])!,
            DataType = Str(r[1])!,
            Nullable = Bool(r[2]),
            Default = Str(r[3]),
            IsPrimaryKey = Bool(r[4]),
            Extra = Str(r[5]) ?? "",
            Comment = Str(r[6]),
        }).ToList();
    }

    public override async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            """
            SELECT i.name, i.is_unique, i.is_primary_key,
                   STRING_AGG(col.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@obj) AND i.name IS NOT NULL
            GROUP BY i.name, i.is_unique, i.is_primary_key
            ORDER BY i.is_primary_key DESC, i.name
            """, ct, ("obj", Qualify(table)));
        return rows.Select(r => new IndexInfo
        {
            Name = Str(r[0])!,
            IsUnique = Bool(r[1]),
            IsPrimary = Bool(r[2]),
            Columns = Str(r[3]) ?? "",
        }).ToList();
    }

    public override async Task<string> GetCreateStatementAsync(TableRef table, CancellationToken ct = default)
    {
        if (!table.IsView) return await base.GetCreateStatementAsync(table, ct);
        var rows = await QueryRowsAsync("SELECT OBJECT_DEFINITION(OBJECT_ID(@obj))", ct, ("obj", Qualify(table)));
        return Str(rows[0][0]) ?? "";
    }

    public override async Task<Dictionary<string, List<string>>> GetAllColumnNamesAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = await QueryRowsAsync(
            "SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION", ct);
        foreach (var r in rows)
        {
            var schema = Str(r[0]);
            var key = schema == "dbo" ? Str(r[1])! : schema + "." + Str(r[1]);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(Str(r[2])!);
        }
        return map;
    }
}
