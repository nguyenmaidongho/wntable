using WNTable.Core;

namespace WNTable.Core.Tests;

public class SqlSplitterTests
{
    private static List<string> Texts(string sql, DbKind kind) => SqlSplitter.Split(sql, kind).Select(s => s.Text).ToList();

    [Fact]
    public void SplitsOnSemicolons()
    {
        Assert.Equal(["SELECT 1", "SELECT 2"], Texts("SELECT 1;\n SELECT 2;", DbKind.MySql));
    }

    [Fact]
    public void IgnoresSemicolonsInStringsAndComments()
    {
        var sql = "SELECT 'a;b', \"c;d\", `e;f` -- x;y\n FROM t /* ; */; SELECT 'it\\'s;'";
        var parts = Texts(sql, DbKind.MySql);
        Assert.Equal(2, parts.Count);
        Assert.StartsWith("SELECT 'a;b'", parts[0]);
        Assert.Equal("SELECT 'it\\'s;'", parts[1]);
    }

    [Fact]
    public void PostgresBackslashIsNotAnEscape()
    {
        Assert.Equal(["SELECT 'C:\\'", "SELECT 2"], Texts("SELECT 'C:\\'; SELECT 2", DbKind.PostgreSql));
    }

    [Fact]
    public void PostgresDollarQuotes()
    {
        var sql = "CREATE FUNCTION f() RETURNS int AS $$ BEGIN RETURN 1; END; $$ LANGUAGE plpgsql; SELECT f();";
        var parts = Texts(sql, DbKind.PostgreSql);
        Assert.Equal(2, parts.Count);
        Assert.EndsWith("LANGUAGE plpgsql", parts[0]);
    }

    [Fact]
    public void MySqlDelimiter()
    {
        var sql = "DELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END//\nDELIMITER ;\nCALL p();";
        var parts = Texts(sql, DbKind.MySql);
        Assert.Equal(["CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END", "CALL p()"], parts);
    }

    [Fact]
    public void SqlServerSplitsOnGoOnly()
    {
        var parts = Texts("SELECT 1; SELECT 2\nGO\nSELECT 3\ngo", DbKind.SqlServer);
        Assert.Equal(["SELECT 1; SELECT 2", "SELECT 3"], parts);
    }

    [Fact]
    public void CommentOnlySegmentsAreDropped()
    {
        Assert.Equal(["SELECT 1"], Texts("SELECT 1; -- done\n", DbKind.MySql));
    }

    [Fact]
    public void StatementAtCaret()
    {
        var sql = "SELECT 1;\nSELECT 2;\n";
        Assert.Equal("SELECT 1", SqlSplitter.StatementAt(sql, 3, DbKind.MySql)!.Value.Text);
        Assert.Equal("SELECT 2", SqlSplitter.StatementAt(sql, 12, DbKind.MySql)!.Value.Text);
        // Caret on the empty line after the last ';' still picks the last statement.
        Assert.Equal("SELECT 2", SqlSplitter.StatementAt(sql, sql.Length, DbKind.MySql)!.Value.Text);
    }
}
