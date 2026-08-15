using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pfandwerk.Core.Data;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options) + "\n");
    }

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"{path} deserialized to null.");

    /// <summary>SHA-256 of the file's bytes, lowercase hex. What plan.approved binds.</summary>
    public static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>
/// Canonical string form for ledger storage and comparison. Without a pinned form,
/// collision checks and identity reuse break on decimals, dates and trailing zeros.
/// </summary>
public static class Canonical
{
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
}

// ---------------------------------------------------------------- artifact shapes

public sealed record GapRow(Dictionary<string, string> Key, string? Current,
                            Dictionary<string, string?>? Inputs);

public sealed record RuleGaps(string Id, string Table, string Column, string Kind,
                              int Count, int Threshold, List<GapRow> Rows);

public sealed record GapsDocument(string RunId, string RulesSha, List<RuleGaps> Rules);

public sealed record CheckResult(string Name, bool Passed, string? Detail);

public sealed record BaselineDocument(string RunId, List<CheckResult> Invariants,
                                      List<CheckResult> Consumer);

public sealed record PlannedPatch(Dictionary<string, string> Key, string Value,
                                  Dictionary<string, string?>? Inputs);

public sealed record SkippedRow(Dictionary<string, string> Key, string Why, string Policy);

public sealed record NewIdentity(Dictionary<string, string> Key, string Value);

public sealed record RulePlan(string Id, string Table, string Column, string Kind,
                              string Status, int Count, int Threshold, string Reason,
                              List<PlannedPatch> Patches, List<SkippedRow> Skipped,
                              List<NewIdentity> NewIdentities);

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
