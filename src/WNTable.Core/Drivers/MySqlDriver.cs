using System.Data.Common;
using MySqlConnector;

namespace WNTable.Core.Drivers;

public sealed class MySqlDriver(ConnectionInfo info) : DbDriver(info)
{
    public override DbKind Kind => DbKind.MySql;

    protected override DbConnection CreateConnection()
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = Info.Host,
            Port = (uint)(Info.Port > 0 ? Info.Port : 3306),
            UserID = Info.User,
            Password = Info.Password,
            Database = CurrentDatabase,
            AllowUserVariables = true,
            AllowPublicKeyRetrieval = true,
            ConvertZeroDateTime = true,
            ConnectionTimeout = 10,
            SslMode = MySqlSslMode.Preferred,
        };
        return new MySqlConnection(b.ConnectionString);
    }

    public override string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public override async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync("SHOW DATABASES", ct);
        return rows.Select(r => Str(r[0])!).ToList();
    }

    public override async Task<IReadOnlyList<TableRef>> GetTablesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(CurrentDatabase)) return [];
        var rows = await QueryRowsAsync(
            "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME", ct);
        return rows.Select(r => MakeTable(null, Str(r[0])!, Str(r[1]) == "VIEW")).ToList();
    }

    public override async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA, COLUMN_COMMENT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t
            ORDER BY ORDINAL_POSITION
            """, ct, ("t", table.Name));
        return rows.Select(r => new ColumnInfo
        {
            Name = Str(r[0])!,
            DataType = Str(r[1])!,
            Nullable = Str(r[2]) == "YES",
            Default = Str(r[3]),
            IsPrimaryKey = Str(r[4]) == "PRI",
            Extra = Str(r[5]) ?? "",
            Comment = Str(r[6]),
        }).ToList();
    }

    public override async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync(
            """
            SELECT INDEX_NAME, MIN(NON_UNIQUE), GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ', ')
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t
            GROUP BY INDEX_NAME
            ORDER BY INDEX_NAME = 'PRIMARY' DESC, INDEX_NAME
            """, ct, ("t", table.Name));
        return rows.Select(r => new IndexInfo
        {
            Name = Str(r[0])!,
            IsUnique = !Bool(r[1]),
            IsPrimary = Str(r[0]) == "PRIMARY",
            Columns = Str(r[2]) ?? "",
        }).ToList();
    }

    public override async Task<string> GetCreateStatementAsync(TableRef table, CancellationToken ct = default)
    {
        var rows = await QueryRowsAsync($"SHOW CREATE TABLE {Quote(table.Name)}", ct);
        return rows.Count > 0 ? Str(rows[0][1]) + ";" : "";
    }

    public override async Task<Dictionary<string, List<string>>> GetAllColumnNamesAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(CurrentDatabase)) return map;
        var rows = await QueryRowsAsync(
            "SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME, ORDINAL_POSITION", ct);
        foreach (var r in rows)
        {
            var t = Str(r[0])!;
            if (!map.TryGetValue(t, out var list)) map[t] = list = [];
            list.Add(Str(r[1])!);
        }
        return map;
    }
}
