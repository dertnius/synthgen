using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Pfandwerk.Core.Data;

public sealed record AllowlistEntry(string Server, string Database, string Auth);

/// <summary>
/// Hard rule 5. Entries are matched canonically on Server + Database + auth mode — never by
/// raw string comparison, so casing, whitespace and key order do not matter.
/// </summary>
public sealed class ConnectionAllowlist
{
    private static readonly string[] CredentialKeys = { "password", "pwd", "token", "secret" };

    public IReadOnlyList<AllowlistEntry> Targets { get; }
    public IReadOnlyList<AllowlistEntry> Ledger { get; }

    private ConnectionAllowlist(List<AllowlistEntry> targets, List<AllowlistEntry> ledger) =>
        (Targets, Ledger) = (targets, ledger);

    public static ConnectionAllowlist Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var targets = ReadSection(doc.RootElement, "targets");
        var ledger = ReadSection(doc.RootElement, "ledger");

        // Scans entry VALUES, not the raw file text. A whole-file grep would match the
        // comment block documenting this rule and reject the file it describes.
        foreach (var e in targets.Concat(ledger))
        {
            foreach (var field in new[] { e.Server, e.Database, e.Auth })
            {
                if (CredentialKeys.Any(k => field.Contains(k + "=", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"allowlist entry contains a credential ('{field}'). This file is committed; " +
                        "entries carry Server + Database + auth mode only.");
            }
        }
        return new ConnectionAllowlist(targets, ledger);
    }

    private static List<AllowlistEntry> ReadSection(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr)
            ? arr.EnumerateArray().Select(e => new AllowlistEntry(
                e.GetProperty("server").GetString() ?? "",
                e.GetProperty("database").GetString() ?? "",
                e.GetProperty("auth").GetString() ?? "")).ToList()
            : new List<AllowlistEntry>();

    public bool IsAllowed(string connectionString, bool ledger, out string detail)
    {
        var list = ledger ? Ledger : Targets;
        var b = new SqlConnectionStringBuilder(connectionString);
        var auth = b.IntegratedSecurity ? "integrated" : "sql";

        foreach (var e in list)
        {
            if (Same(e.Server, b.DataSource) && Same(e.Database, b.InitialCatalog) && Same(e.Auth, auth))
            {
                detail = $"{b.DataSource}/{b.InitialCatalog} ({auth})";
                return true;
            }
        }

        detail = $"{b.DataSource}/{b.InitialCatalog} ({auth}) is not in the {(ledger ? "ledger" : "targets")} allowlist";
        return false;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
