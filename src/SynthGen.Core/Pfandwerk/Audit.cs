using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pfandwerk;

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

    /// <summary>
    /// Truncates defensively. The fallback is what ships when everything else has failed,
    /// so it must never be the thing that throws — a short or empty hash renders as-is.
    /// </summary>
    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha.Length <= 12 ? sha : sha[..12];

    /// <summary>Bare rendering of facts.json, published when the narrative fails audit twice.</summary>
    public static string Fallback(FactsDocument f)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Run report (narrative failed audit)");
        sb.AppendLine();
        sb.AppendLine($"Run `{f.RunId}` · plan `{Short(f.PlanSha)}` · rules `{Short(f.RulesSha)}` · " +
                      $"approved by {f.Approver} at {f.Ts}");
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


/// <summary>
/// The CI notary. Recomputes what it can from the committed artifacts and cross-checks the
/// rest, with no database and no Copilot.
///
/// <para>Scope, stated plainly (D13): this verifies that a set of files produced by one
/// actor is internally consistent. It catches drift, a stale report, an unapproved plan
/// and a same-person approval. It cannot catch someone who edits patches.jsonl and
/// facts.json together — that is not the threat it is built for.</para>
/// </summary>
public static class ArtifactAuditor
{
    public static AuditDocument Audit(string artifactsDir, string rulesPath, string? repoRoot = null)
    {
        var v = new List<AuditViolation>();
        string P(string name) => Path.Combine(artifactsDir, name);

        foreach (var required in new[] { "plan.json", "plan.approved", "patches.jsonl", "verify.json", "facts.json" })
        {
            if (!File.Exists(P(required)))
                v.Add(new AuditViolation("missing-artifact", $"{required} was not committed."));
        }
        if (v.Count > 0) return new AuditDocument("fail", v);

        // 1. The plan that ran is the plan that was approved.
        var approval = Json.Read<Approval>(P("plan.approved"));
        var planSha = Json.Sha256File(P("plan.json"));
        if (!string.Equals(planSha, approval.Sha256, StringComparison.OrdinalIgnoreCase))
            v.Add(new AuditViolation("plan-hash",
                $"plan.json hashes to {planSha[..12]} but plan.approved records {approval.Sha256[..12]}."));

        // 2. The rules that produced the plan are the rules in the tree.
        var plan = Json.Read<PlanDocument>(P("plan.json"));
        var rulesSha = Json.Sha256File(rulesPath);
        if (!string.Equals(plan.RulesSha, rulesSha, StringComparison.OrdinalIgnoreCase))
            v.Add(new AuditViolation("rules-drift",
                $"plan.json was built from rules {plan.RulesSha[..12]}, but {rulesPath} hashes to {rulesSha[..12]}."));

        // 3. facts.json is reproducible from the artifacts it claims to summarise.
        var committed = Json.Read<FactsDocument>(P("facts.json"));
        var recomputed = FactExtractor.Extract(P("patches.jsonl"), P("verify.json"),
                                               P("plan.json"), P("plan.approved"));
        foreach (var diff in DiffFacts(committed, recomputed)) v.Add(diff);

        // 4. The report, if published, is sourced from facts.json.
        if (File.Exists(P("report.md")))
        {
            foreach (var rv in ReportAuditor.Audit(P("report.md"), P("facts.json")).Violations)
                v.Add(rv);
        }

        // 5. Four eyes: the approver is not the person who last changed the rules (D11).
        //    "Rules" spans the rules file AND its sibling datasets/ directory — a reviewed
        //    vocabulary decides patched values exactly like a rule does (D-A1), so its
        //    author is a rules author. With an agent committing as the pinned bot identity
        //    (D-C1), this same comparison is what makes bot-author vs human-approver real.
        if (repoRoot is not null)
        {
            foreach (var source in RuleSources(rulesPath))
            {
                var author = LastCommitAuthor(repoRoot, source);
                if (author is not null &&
                    string.Equals(author, approval.GitEmail, StringComparison.OrdinalIgnoreCase))
                {
                    v.Add(new AuditViolation("four-eyes",
                        $"{approval.GitEmail} approved a run using rules they themselves last changed ({source})."));
                }
            }
        }

        return new AuditDocument(v.Count == 0 ? "pass" : "fail", v);
    }

    private static IEnumerable<AuditViolation> DiffFacts(FactsDocument committed, FactsDocument recomputed)
    {
        if (committed.NewIdentities.Count != recomputed.NewIdentities.Count)
            yield return new AuditViolation("facts-drift",
                $"facts.json lists {committed.NewIdentities.Count} new identities; the artifacts yield {recomputed.NewIdentities.Count}.");

        foreach (var rule in recomputed.Rules)
        {
            var c = committed.Rules.FirstOrDefault(x => x.Id == rule.Id);
            if (c is null)
            {
                yield return new AuditViolation("facts-drift", $"facts.json is missing rule '{rule.Id}'.");
                continue;
            }
            if (c.Patched != rule.Patched)
                yield return new AuditViolation("facts-drift",
                    $"facts.json says {rule.Id} patched {c.Patched} rows; patches.jsonl contains {rule.Patched}.");
        }

        if (committed.Verify.Regressions.Count != recomputed.Verify.Regressions.Count)
            yield return new AuditViolation("facts-drift", "facts.json regression list does not match verify.json.");
    }

    /// <summary>The rules file, plus its datasets/ directory when one exists.</summary>
    private static IEnumerable<string> RuleSources(string rulesPath)
    {
        yield return rulesPath;
        var datasets = Path.Combine(Path.GetDirectoryName(rulesPath) ?? ".", "datasets");
        if (Directory.Exists(datasets)) yield return datasets;
    }

    /// <summary>Email of whoever last touched the rules file, or null outside a git tree.</summary>
    private static string? LastCommitAuthor(string repoRoot, string path)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "log", "-1", "--format=%ae", "--", path }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;   // Not a git checkout; the four-eyes check simply does not apply.
        }
    }
}
