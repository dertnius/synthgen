using System.Text.Json;

namespace Pfandwerk;


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
