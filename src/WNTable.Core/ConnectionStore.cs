using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WNTable.Core;

/// <summary>Saved connections in %APPDATA%\WNTable\connections.json. Passwords are encrypted with Windows DPAPI.</summary>
public sealed class ConnectionStore(string? directory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; } = directory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WNTable");

    private string FilePath => Path.Combine(Directory, "connections.json");

    public List<ConnectionInfo> Load()
    {
        if (!File.Exists(FilePath)) return [];
        var list = JsonSerializer.Deserialize<List<ConnectionInfo>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        foreach (var c in list)
        {
            c.Password = Unprotect(c.ProtectedPassword);
        }
        return list;
    }

    public void Save(IEnumerable<ConnectionInfo> connections)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var list = connections.ToList();
        foreach (var c in list)
        {
            c.ProtectedPassword = Protect(c.Password);
        }
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Last text of the query editor for a connection, restored on the next open.</summary>
    public string LoadScratch(string connectionId)
    {
        var path = Path.Combine(Directory, "queries", connectionId + ".sql");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveScratch(string connectionId, string text)
    {
        var dir = Path.Combine(Directory, "queries");
        System.IO.Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, connectionId + ".sql"), text);
    }

    private static string? Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Encrypted by another Windows user / machine: the user has to retype it.
            return "";
        }
    }
}
