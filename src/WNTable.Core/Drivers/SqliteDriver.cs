using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace WNTable.Core.Drivers;

public sealed class SqliteDriver : DbDriver
{
    public SqliteDriver(ConnectionInfo info) : base(info)
    {
        CurrentDatabase = "main";
    }

    public override DbKind Kind => DbKind.Sqlite;
    public override bool SupportsDatabases => false;

    protected override DbConnection CreateConnection()
    {
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = Info.FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        };
        return new SqliteConnection(b.ConnectionString);
    }

    public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public override Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(["main"]);

    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            "SELECT name, type FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name", ct);
        return rows.Select(r => MakeTable(null, Str(r[0])!, Str(r[1]) == "view")).ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync($"PRAGMA table_info({Quote(table.Name)})", ct);
        var pkCount = rows.Count(r => Convert.ToInt64(r[5]) > 0);
        return rows.Select(r =>
        {
            var isPk = Convert.ToInt64(r[5]) > 0;
            var type = Str(r[2]) ?? "";
            return new ColumnInfo
            {
                Name = Str(r[1])!,
                DataType = type,
                Nullable = !Bool(r[3]) && !isPk,
                Default = Str(r[4]),
                IsPrimaryKey = isPk,
                Extra = isPk && pkCount == 1 && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ? "rowid" : "",
            };
        }).ToList();
    }

    public override async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var list = await QueryRowsAsync(conn, $"PRAGMA index_list({Quote(table.Name)})", ct);
        var result = new List<IndexInfo>();
        foreach (var r in list)
        {
            var name = Str(r[1])!;
            var cols = await QueryRowsAsync(conn, $"PRAGMA index_info({Quote(name)})", ct);
            result.Add(new IndexInfo
            {
                Name = name,
                IsUnique = Bool(r[2]),
                IsPrimary = Str(r[3]) == "pk",
                Columns = string.Join(", ", cols.Select(c => Str(c[2]))),
            });
        }
        if (!result.Any(i => i.IsPrimary))
        {
            // INTEGER PRIMARY KEY is the rowid itself and has no index entry; show it anyway.
            var pk = (await GetColumnsAsync(table, ct)).Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (pk.Count > 0)
            {
                result.Insert(0, new IndexInfo { Name = "PRIMARY KEY", Columns = string.Join(", ", pk), IsUnique = true, IsPrimary = true });
            }
        }
        return result;
    }

    public override async Task<string> GetCreateStatementAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            "SELECT sql FROM sqlite_master WHERE tbl_name = @n AND sql IS NOT NULL ORDER BY type = 'table' DESC, name", ct, ("n", table.Name));
        return string.Join(";\n\n", rows.Select(r => Str(r[0]))) + ";";
    }
}
