using System.Data;
using WNTable.Core;

namespace WNTable.Core.Tests;

/// <summary>Round-trip through a real engine: schema, paging, and grid edits saved back.</summary>
public abstract class DriverTestsBase
{
    protected abstract DbDriver? CreateDriver();
    protected abstract string CreateTableSql { get; }

    /// <summary>Null when the server is not reachable on this machine (the test then has nothing to check).</summary>
    private async Task<(DbDriver Driver, TableRef Table)?> SetupAsync()
    {
        var driver = CreateDriver();
        if (driver is null) return null;
        await using (var conn = await driver.OpenAsync())
        {
            var statements = SqlSplitter.Split(
                "DROP TABLE IF EXISTS wn_items;" + CreateTableSql +
                "INSERT INTO wn_items (id, name, price, note) VALUES (1, 'Táo', 10.5, NULL), (2, 'Lê', 20, 'x;y'), (3, 'Cam', 30, 'z');",
                driver.Kind).Select(s => s.Text).ToList();
            await driver.ExecuteAsync(conn, statements, 100, CancellationToken.None);
        }
        var table = (await driver.GetTablesAsync()).Single(t => t.Name == "wn_items");
        return (driver, table);
    }

    private static async Task<DataTable> LoadAsync(DbDriver driver, TableRef table, string? where = null)
    {
        await using var conn = await driver.OpenAsync();
        var sql = driver.BuildSelect(table, where, driver.Quote("id"), 100, 0);
        var result = await driver.ExecuteAsync(conn, [sql], 100, CancellationToken.None);
        return result.Tables.Single();
    }

    [Fact]
    public async Task ReadsSchema()
    {
        if (await SetupAsync() is not var (driver, table)) return;
        var cols = await driver.GetColumnsAsync(table);
        Assert.Equal(["id", "name", "price", "note"], cols.Select(c => c.Name));
        Assert.True(cols[0].IsPrimaryKey);
        Assert.False(cols[1].IsPrimaryKey);
        var indexes = await driver.GetIndexesAsync(table);
        Assert.Contains(indexes, i => i.IsPrimary);
        Assert.Contains("wn_items", await driver.GetCreateStatementAsync(table));
        var all = await driver.GetAllColumnNamesAsync();
        Assert.Contains("price", all["wn_items"]);
    }

    [Fact]
    public async Task PagesAndFilters()
    {
        if (await SetupAsync() is not var (driver, table)) return;
        var data = await LoadAsync(driver, table, $"{driver.Quote("price")} > 15");
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal("name", data.Columns[1].Caption);
        Assert.Equal("Lê", data.Rows[0][1]);
    }

    [Fact]
    public async Task SavesGridEdits()
    {
        if (await SetupAsync() is not var (driver, table)) return;
        var cols = await driver.GetColumnsAsync(table);
        var data = await LoadAsync(driver, table);

        // Edits arrive from the grid as strings.
        data.Rows[0][1] = "Táo xanh";
        data.Rows[0][3] = "ghi chú 'nháy'";
        // Cell entered and left unchanged: the grid writes back the displayed text, which is not a change.
        data.Rows[1][2] = ValueFormatter.Format(data.Rows[1][2]);
        data.Rows[2].Delete();
        var added = data.NewRow();
        added[0] = "4";
        added[1] = "Nho";
        added[2] = "40.25";
        data.Rows.Add(added);

        Assert.Equal(3, ChangeBuilder.CountChanges(data));
        var statements = ChangeBuilder.Build(driver, table, data, cols);
        Assert.Equal(3, statements.Count);
        await driver.ApplyChangesAsync(statements);

        var after = await LoadAsync(driver, table);
        Assert.Equal(["1", "2", "4"], after.Rows.Cast<DataRow>().Select(r => ValueFormatter.Format(r[0])));
        Assert.Equal("Táo xanh", after.Rows[0][1]);
        Assert.Equal("ghi chú 'nháy'", after.Rows[0][3]);
        Assert.Equal("Nho", after.Rows[2][1]);
        Assert.Equal(40.25m, Convert.ToDecimal(after.Rows[2][2]));
    }

    [Fact]
    public async Task FailedSaveRollsBack()
    {
        if (await SetupAsync() is not var (driver, table)) return;
        var cols = await driver.GetColumnsAsync(table);
        var data = await LoadAsync(driver, table);
        data.Rows[0][1] = "đổi";
        data.Rows[1][0] = "999"; // the WHERE uses the original id, so this works…
        var ghost = data.Rows[2];
        ghost.Delete();
        var statements = ChangeBuilder.Build(driver, table, data, cols);

        // …but make one row vanish first so its DELETE matches nothing.
        await using (var conn = await driver.OpenAsync())
        {
            await driver.ExecuteAsync(conn, [$"DELETE FROM wn_items WHERE id = 3"], 1, CancellationToken.None);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ApplyChangesAsync(statements));
        var after = await LoadAsync(driver, table);
        Assert.Equal("Táo", after.Rows[0][1]);
    }
}

public class SqliteDriverTests : DriverTestsBase
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wntable-{Guid.NewGuid():N}.db");

    protected override DbDriver CreateDriver() => DbDriver.Create(new ConnectionInfo { Kind = DbKind.Sqlite, FilePath = _path });

    protected override string CreateTableSql =>
        "CREATE TABLE wn_items (id INTEGER PRIMARY KEY, name TEXT NOT NULL, price NUMERIC, note TEXT); CREATE INDEX ix_name ON wn_items(name);";
}

/// <summary>Uses the local Laragon MySQL (root, no password) and only touches the wntable_test database.</summary>
public class MySqlDriverTests : DriverTestsBase
{
    protected override DbDriver? CreateDriver()
    {
        var info = new ConnectionInfo { Kind = DbKind.MySql, Host = "127.0.0.1", Port = 3306, User = "root" };
        var driver = DbDriver.Create(info);
        try
        {
            using var conn = driver.OpenAsync().GetAwaiter().GetResult();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE DATABASE IF NOT EXISTS wntable_test CHARACTER SET utf8mb4";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            return null;
        }
        driver.CurrentDatabase = "wntable_test";
        return driver;
    }

    protected override string CreateTableSql =>
        "CREATE TABLE wn_items (id INT PRIMARY KEY, name VARCHAR(100) NOT NULL, price DECIMAL(10,2), note TEXT, KEY ix_name (name));";
}
