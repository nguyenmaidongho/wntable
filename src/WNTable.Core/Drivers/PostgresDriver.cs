using System.Data.Common;
using Npgsql;

namespace WNTable.Core.Drivers;

public sealed class PostgresDriver : DbDriver
{
    private const string UserSchemas = "NOT IN ('pg_catalog', 'information_schema') AND {0} NOT LIKE 'pg\\_toast%' AND {0} NOT LIKE 'pg\\_temp%'";

    public PostgresDriver(ConnectionInfo info) : base(info)
    {
        if (string.IsNullOrEmpty(CurrentDatabase)) CurrentDatabase = "postgres";
    }

    public override DbKind Kind => DbKind.PostgreSql;
    public override string? DefaultSchema => "public";

    protected override DbConnection CreateConnection()
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Info.Host,
            Port = Info.Port > 0 ? Info.Port : 5432,
            Username = Info.User,
            Password = Info.Password,
            Database = CurrentDatabase,
            Timeout = 10,
            IncludeErrorDetail = true,
        };
        return new NpgsqlConnection(b.ConnectionString);
    }

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public override string Placeholder(string name, ColumnInfo? column) =>
        column is null ? "@" + name : $"@{name}::{column.DataType}";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync("SELECT datname FROM pg_database WHERE NOT datistemplate AND datallowconn ORDER BY datname", ct);
        return rows.Select(r => Str(r[0])!).ToList();
    }

    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            $"""
            SELECT * FROM (
                SELECT table_schema::text AS s, table_name::text AS n, table_type::text AS k FROM information_schema.tables
                WHERE table_schema {string.Format(UserSchemas, "table_schema")}
                UNION ALL
                SELECT schemaname::text, matviewname::text, 'VIEW' FROM pg_matviews
            ) t
            ORDER BY s <> 'public', s, n
            """, ct);
        return rows.Select(r => MakeTable(Str(r[0]), Str(r[1])!, Str(r[2]) != "BASE TABLE")).ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull, pg_get_expr(d.adbin, d.adrelid),
                   EXISTS (SELECT 1 FROM pg_index i WHERE i.indrelid = a.attrelid AND i.indisprimary AND a.attnum = ANY(i.indkey)),
                   CASE WHEN a.attidentity <> '' THEN 'identity' WHEN a.attgenerated <> '' THEN 'generated' ELSE '' END,
                   col_description(a.attrelid, a.attnum)
            FROM pg_attribute a
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attrelid = @rel::regclass AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, ct, ("rel", Qualify(table)));
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
            SELECT ic.relname, i.indisunique, i.indisprimary, pg_get_indexdef(i.indexrelid)
            FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid
            WHERE i.indrelid = @rel::regclass
            ORDER BY i.indisprimary DESC, ic.relname
            """, ct, ("rel", Qualify(table)));
        return rows.Select(r =>
        {
            var def = Str(r[3]) ?? "";
            var using_ = def.IndexOf(" USING ", StringComparison.Ordinal);
            return new IndexInfo
            {
                Name = Str(r[0])!,
                IsUnique = Bool(r[1]),
                IsPrimary = Bool(r[2]),
                Columns = using_ >= 0 ? def[(using_ + 7)..] : def,
            };
        }).ToList();
    }

    public override async Task<string> GetCreateStatementAsync(TableRef table, CancellationToken ct = default)
    {
        if (table.IsView)
        {
            var rows = await QueryRowsAsync("SELECT pg_get_viewdef(@rel::regclass, true)", ct, ("rel", Qualify(table)));
            return $"CREATE VIEW {Qualify(table)} AS\n{Str(rows[0][0])}";
        }
        var ddl = await base.GetCreateStatementAsync(table, ct);
        var indexes = await QueryRowsAsync(
            "SELECT pg_get_indexdef(indexrelid) FROM pg_index WHERE indrelid = @rel::regclass AND NOT indisprimary", ct, ("rel", Qualify(table)));
        return indexes.Count == 0 ? ddl : ddl + "\n\n" + string.Join(";\n", indexes.Select(r => Str(r[0]))) + ";";
    }

    public override async Task<Dictionary<string, List<string>>> GetAllColumnNamesAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var rows = await QueryRowsAsync(
            $"""
            SELECT table_schema, table_name, column_name FROM information_schema.columns
            WHERE table_schema {string.Format(UserSchemas, "table_schema")}
            ORDER BY table_schema, table_name, ordinal_position
            """, ct);
        foreach (var r in rows)
        {
            var schema = Str(r[0]);
            var key = schema == "public" ? Str(r[1])! : schema + "." + Str(r[1]);
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(Str(r[2])!);
        }
        return map;
    }
}
