using System.Data;
using System.Text.Json.Serialization;

namespace WNTable.Core;

public enum DbKind
{
    MySql,
    PostgreSql,
    Sqlite,
    SqlServer,
}

public sealed class ConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DbKind Kind { get; set; } = DbKind.MySql;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "";
    public string Database { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>Hex color tag (e.g. "#E5484D") shown on the workspace, TablePlus-style.</summary>
    public string Color { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;
    /// <summary>DPAPI-protected password, base64. Only <see cref="Password"/> is used at runtime.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public string Password { get; set; } = "";

    public ConnectionInfo Clone() => (ConnectionInfo)MemberwiseClone();

    [JsonIgnore]
    public string Subtitle => Kind == DbKind.Sqlite
        ? FilePath
        : $"{(string.IsNullOrEmpty(User) ? "" : User + "@")}{Host}:{Port}{(string.IsNullOrEmpty(Database) ? "" : "/" + Database)}";

    [JsonIgnore]
    public string KindLabel => KindName(Kind);

    public static string KindName(DbKind kind) => kind switch
    {
        DbKind.MySql => "MySQL / MariaDB",
        DbKind.PostgreSql => "PostgreSQL",
        DbKind.Sqlite => "SQLite",
        DbKind.SqlServer => "SQL Server",
        _ => kind.ToString(),
    };

    public static int DefaultPort(DbKind kind) => kind switch
    {
        DbKind.MySql => 3306,
        DbKind.PostgreSql => 5432,
        DbKind.SqlServer => 1433,
        _ => 0,
    };
}

public sealed record TableRef(string? Schema, string Name, bool IsView, string Display);

public sealed class ColumnInfo
{
    public string Name { get; init; } = "";
    public string DataType { get; init; } = "";
    public bool Nullable { get; init; }
    public string? Default { get; init; }
    public bool IsPrimaryKey { get; init; }
    public string Extra { get; init; } = "";
    public string? Comment { get; init; }
}

public sealed class IndexInfo
{
    public string Name { get; init; } = "";
    public string Columns { get; init; } = "";
    public bool IsUnique { get; init; }
    public bool IsPrimary { get; init; }
}

public sealed class ExecuteResult
{
    public List<DataTable> Tables { get; } = [];
    public List<string> Messages { get; } = [];
    public int RecordsAffected { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool Truncated { get; set; }
}

public sealed record SqlStatement(string Sql, IReadOnlyList<KeyValuePair<string, object>> Parameters, string Preview);
