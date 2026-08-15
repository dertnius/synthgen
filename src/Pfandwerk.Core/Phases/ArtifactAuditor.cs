using System.Diagnostics;
using Pfandwerk.Core.Data;

namespace Pfandwerk.Core.Phases;

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
        if (repoRoot is not null)
        {
            var author = LastCommitAuthor(repoRoot, rulesPath);
            if (author is not null &&
                string.Equals(author, approval.GitEmail, StringComparison.OrdinalIgnoreCase))
            {
                v.Add(new AuditViolation("four-eyes",
                    $"{approval.GitEmail} approved a run using rules they themselves last changed."));
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
