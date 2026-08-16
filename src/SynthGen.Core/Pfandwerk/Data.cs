using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace Pfandwerk;

/// <summary>
/// The one home for value conversions between artifacts (strings), the database, SQL
/// text, and JSON. Values round-trip through plan.json and patches.jsonl as strings;
/// every direction out of that form lives here so the heuristics cannot drift apart.
/// </summary>
public static class Canonical
{
    /// <summary>
    /// Canonical string form for ledger storage and comparison. Without a pinned form,
    /// collision checks and identity reuse break on decimals, dates and trailing zeros.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Parameter value for the driver: hand it a number when the string is one.</summary>
    public static object DbValue(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : value;

    /// <summary>Inline SQL literal: numeric keys must not be quoted.</summary>
    public static string SqlLiteral(string key) =>
        long.TryParse(key, out _) ? key : "'" + key.Replace("'", "''") + "'";

    /// <summary>
    /// Relaxed escaping for JSON that goes into an HTTP request body, never into HTML.
    /// The default encoder escapes '+' to +, which is valid JSON but turns a real
    /// EnergyClass value like "A+" into something unreadable on the wire and in logs.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions RelaxedJson =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// JSON-typed rendering for DAB's request-body-strict endpoints: numeric-looking
    /// values become JSON numbers.
    ///
    /// <para><b>Known failure mode:</b> a string column whose value happens to be all
    /// digits is sent as a number. SqlPatchSink's <see cref="DbValue"/> has the same
    /// heuristic but the driver reconciles the mismatch; DAB does not. Use --sink sql for
    /// such a column.</para>
    /// </summary>
    public static string JsonValue(string? value)
    {
        if (value is null) return "null";
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d.ToString(CultureInfo.InvariantCulture);
        return System.Text.Json.JsonSerializer.Serialize(value, RelaxedJson);
    }
}

// ---------------------------------------------------------------- artifact shapes


public sealed record CheckResult(string Name, bool Passed, string? Detail);

public sealed record BaselineDocument(string RunId, List<CheckResult> Invariants,
                                      List<CheckResult> Consumer);

public sealed record PlannedPatch(Dictionary<string, string> Key, string Value,
                                  Dictionary<string, string?>? Inputs);

public sealed record SkippedRow(Dictionary<string, string> Key, string Why, string Policy);

public sealed record NewIdentity(Dictionary<string, string> Key, string Value);

/// <summary>
/// How well a rule's gap predicate lines up with its invariant. Null when the rule declares
/// no invariant, because there is then nothing to cross-check against.
///
/// <para>All three are advisory. Row 104 of the shipped Security fixture is a legitimate
/// uncovered violation — its inputs are unusable, so no rule can repair it — and refusing
/// that run would be wrong.</para>
/// </summary>
public sealed record RuleCoverage(
    int UncoveredViolations, List<string> UncoveredKeys,
    int SelectedButValid, List<string> SelectedButValidKeys,
    int Indeterminate, List<string> IndeterminateKeys);

public sealed record RulePlan(string Id, string Table, string Column, string Kind,
                              string Status, int Count, int Threshold, string Reason,
                              List<PlannedPatch> Patches, List<SkippedRow> Skipped,
                              List<NewIdentity> NewIdentities, RuleCoverage? Coverage = null);

public sealed record PlanDocument(string RunId, string RulesSha, List<RulePlan> Rules);

public sealed record Approval(string Sha256, string OsUser, string GitEmail, string TimestampUtc);

public sealed record VerifyDocument(string RunId, bool L1, bool L2, bool L3,
                                    List<string> Regressions, List<string> PreexistingReds,
                                    List<CheckResult> Invariants, List<CheckResult> Consumer,
                                    List<string> RemainingPlannedGaps);

public sealed record FactRule(string Id, string Column, string Kind, int Patched,
                              List<string> Samples, string Reason, List<SkippedRow> Skipped);

public sealed record FactsVerify(bool L1, bool L2, bool L3,
                                 List<string> Regressions, List<string> PreexistingReds);

public sealed record FactsDocument(string RunId, string PlanSha, string RulesSha,
                                   string Approver, string Ts, List<FactRule> Rules,
                                   List<NewIdentity> NewIdentities, FactsVerify Verify);

public sealed record AuditViolation(string Kind, string Detail);

public sealed record AuditDocument(string Verdict, List<AuditViolation> Violations);

/// <summary>
/// Reads the .sql files embedded from db/pfandwerk/. They are the source of truth for the
/// ledger schema — one copy a DBA can run by hand, and the same text the tool applies.
/// </summary>
public static class Sql
{
    public static string Read(string name)
    {
        using var stream = typeof(Sql).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource '{name}' is missing");
        return new StreamReader(stream).ReadToEnd();
    }
}

public sealed record LedgerEntry(string TargetTable, string RowKey, string ColumnName,
                                 string Value, string RuleId);

/// <summary>
/// Append-only identity ledger. Rows are never updated or deleted (hard rule 3): a value
/// recorded here is the value that column will carry forever.
/// </summary>
public sealed class LedgerRepository
{
    private readonly DbContext _db;
    public LedgerRepository(DbContext db) => _db = db;

    public void EnsureCreated()
    {
        using var c = _db.OpenLedger();
        c.Execute(Sql.Read(_db.Provider == Provider.Sqlite ? "ledger.sqlite.sql" : "ledger.sql"));
    }

    public string? Lookup(string table, string rowKey, string column)
    {
        using var c = _db.OpenLedger();
        return c.QueryFirstOrDefault<string>(
            "SELECT Value FROM dbo.SyntheticLedger WHERE TargetTable=@t AND RowKey=@k AND ColumnName=@c",
            new { t = table, k = rowKey, c = column });
    }

    public bool ValueTaken(string table, string column, string value)
    {
        using var c = _db.OpenLedger();
        return c.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM dbo.SyntheticLedger WHERE TargetTable=@t AND ColumnName=@c AND Value=@v",
            new { t = table, c = column, v = value }) > 0;
    }

    public int Count()
    {
        using var c = _db.OpenLedger();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SyntheticLedger");
    }

    /// <summary>Value already recorded for this row+column, or null if none.</summary>
    public static string? Existing(IDbConnection conn, IDbTransaction? tx,
                                   string table, string rowKey, string column) =>
        conn.QueryFirstOrDefault<string>(
            "SELECT Value FROM dbo.SyntheticLedger WHERE TargetTable=@t AND RowKey=@k AND ColumnName=@c",
            new { t = table, k = rowKey, c = column }, tx);

    public static void Insert(IDbConnection conn, IDbTransaction? tx, LedgerEntry e, string createdBy) =>
        conn.Execute(
            """
            INSERT INTO dbo.SyntheticLedger (TargetTable, RowKey, ColumnName, Value, RuleId, CreatedBy)
            VALUES (@TargetTable, @RowKey, @ColumnName, @Value, @RuleId, @createdBy)
            """,
            new { e.TargetTable, e.RowKey, e.ColumnName, e.Value, e.RuleId, createdBy }, tx);

    /// <summary>
    /// Appends the ledger row unless it is already there — shared by both sinks so the
    /// conflict semantics cannot drift. A run reusing a frozen identity — after a revert,
    /// or because the column was cleared upstream — must not record it twice; the existing
    /// row is the whole reason the value came back. A <em>different</em> recorded value is
    /// a real conflict and stops the run.
    /// </summary>
    public static void Reserve(IDbConnection conn, IDbTransaction? tx,
                               GapRule rule, string rowKey, string? value, string createdBy)
    {
        var existing = Existing(conn, tx, rule.Table, rowKey, rule.Column);
        if (existing is not null)
        {
            if (!string.Equals(existing, value, StringComparison.Ordinal))
                throw new LedgerConflictException(
                    $"Rule '{rule.Id}': the ledger records {rule.Column} = '{existing}' for " +
                    $"{rule.Key} {rowKey}, but this run would write '{value}'. Ledger rows " +
                    "are never updated, so this needs a human.");
            return;
        }
        Insert(conn, tx, new LedgerEntry(rule.Table, rowKey, rule.Column, value!, rule.Id), createdBy);
    }
}

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
