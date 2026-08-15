using System.Text.Json;
using System.Text.RegularExpressions;
using Pfandwerk.Core.Data;

namespace Pfandwerk.Core.Phases;

/// <summary>
/// Builds facts.json from the run's own artifacts. Deterministic and the load-bearing
/// component: it is the sole ground truth the report is audited against.
/// </summary>
public static class FactExtractor
{
    public static FactsDocument Extract(string patchesPath, string verifyPath,
                                        string planPath, string approvedPath)
    {
        var plan = Json.Read<PlanDocument>(planPath);
        var verify = Json.Read<VerifyDocument>(verifyPath);
        var approval = Json.Read<Approval>(approvedPath);

        var patchedByRule = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(patchesPath).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var doc = JsonDocument.Parse(line);
            var rule = doc.RootElement.GetProperty("rule").GetString()!;
            var value = doc.RootElement.GetProperty("new").ToString();
            if (!patchedByRule.TryGetValue(rule, out var list))
                patchedByRule[rule] = list = new List<string>();
            list.Add(value);
        }

        var rules = plan.Rules.Select(r =>
        {
            var values = patchedByRule.GetValueOrDefault(r.Id) ?? new List<string>();
            return new FactRule(r.Id, r.Column, r.Kind, values.Count,
                                values.Take(3).ToList(), r.Reason, r.Skipped);
        }).ToList();

        var identities = plan.Rules.SelectMany(r => r.NewIdentities).ToList();

        return new FactsDocument(plan.RunId, Json.Sha256File(planPath), plan.RulesSha,
            approval.GitEmail, approval.TimestampUtc, rules, identities,
            new FactsVerify(verify.L1, verify.L2, verify.L3,
                            verify.Regressions, verify.PreexistingReds));
    }
}

/// <summary>
/// Deterministic replacement for the LLM checker (D9). Every number and rule id in the
/// prose must appear in facts.json, and every rule in facts.json must appear in the prose.
/// The same class runs locally as the checker and in CI as the notary.
/// </summary>
public static class ReportAuditor
{
    private static readonly Regex NumberPattern = new(@"(?<![\w.])-?\d+(?:\.\d+)?(?![\w.])", RegexOptions.Compiled);

    public static AuditDocument Audit(string reportPath, string factsPath)
    {
        var report = File.ReadAllText(reportPath);
        var facts = Json.Read<FactsDocument>(factsPath);
        var factsRaw = File.ReadAllText(factsPath);
        var violations = new List<AuditViolation>();

        // Every rule in facts.json must be mentioned.
        foreach (var rule in facts.Rules.Where(r => !report.Contains(r.Id, StringComparison.OrdinalIgnoreCase)))
            violations.Add(new AuditViolation("missing-rule", $"facts.json rule '{rule.Id}' does not appear in the report."));

        // Every identity value must be mentioned — these are permanent and must not be dropped.
        foreach (var id in facts.NewIdentities.Where(i => !report.Contains(i.Value, StringComparison.Ordinal)))
            violations.Add(new AuditViolation("missing-identity", $"new identity '{id.Value}' does not appear in the report."));

        // Every number in the prose must be sourceable from facts.json.
        var known = NumberPattern.Matches(factsRaw).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var n in NumberPattern.Matches(StripCodeFences(report)).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            if (known.Contains(n)) continue;
            violations.Add(new AuditViolation("unsourced-number",
                $"the number '{n}' appears in the report but not in facts.json."));
        }

        return new AuditDocument(violations.Count == 0 ? "pass" : "fail", violations);
    }

    /// <summary>Fenced blocks are quoted artifacts, audited at their source rather than here.</summary>
    private static string StripCodeFences(string markdown) =>
        Regex.Replace(markdown, "```.*?```", "", RegexOptions.Singleline);

    /// <summary>Bare rendering of facts.json, published when the narrative fails audit twice.</summary>
    public static string Fallback(FactsDocument f)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Run report (narrative failed audit)");
        sb.AppendLine();
        sb.AppendLine($"Run `{f.RunId}` · plan `{f.PlanSha[..12]}` · rules `{f.RulesSha[..12]}` · approved by {f.Approver} at {f.Ts}");
        sb.AppendLine();
        // Every figure below is copied from facts.json, never computed here — the fallback
        // holds itself to the same rule the auditor imposes on the maker, so it survives
        // its own audit. A count of skipped rows would be a derived number; the keys are
        // listed instead.
        sb.AppendLine("| Rule | Column | Kind | Patched | Skipped rows |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in f.Rules)
        {
            var skipped = r.Skipped.Count == 0
                ? "none"
                : string.Join("; ", r.Skipped.Select(s => string.Join(", ", s.Key.Select(k => $"{k.Key} {k.Value}"))));
            sb.AppendLine($"| {r.Id} | {r.Column} | {r.Kind} | {r.Patched} | {skipped} |");
        }
        sb.AppendLine();
        sb.AppendLine("## New identities");
        sb.AppendLine();
        if (f.NewIdentities.Count == 0) sb.AppendLine("None.");
        else foreach (var i in f.NewIdentities)
            sb.AppendLine($"- {string.Join(", ", i.Key.Select(k => $"{k.Key} {k.Value}"))} -> `{i.Value}` (permanent)");
        sb.AppendLine();
        sb.AppendLine($"## Verify");
        sb.AppendLine();
        sb.AppendLine($"- L1 re-scan:  {(f.Verify.L1 ? "pass" : "FAIL")}");
        sb.AppendLine($"- L2 invariants: {(f.Verify.L2 ? "pass" : "FAIL")}");
        sb.AppendLine($"- L3 consumer: {(f.Verify.L3 ? "pass" : "FAIL")}");
        sb.AppendLine($"- regressions: {(f.Verify.Regressions.Count == 0 ? "none" : string.Join(", ", f.Verify.Regressions))}");
        sb.AppendLine($"- pre-existing failures: {(f.Verify.PreexistingReds.Count == 0 ? "none" : string.Join(", ", f.Verify.PreexistingReds))}");
        return sb.ToString();
    }
}
