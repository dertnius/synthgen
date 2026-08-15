using Dapper;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Rules;

namespace Pfandwerk.Core.Phases;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int ConfigError = 2;
    public const int RuntimeError = 3;
    public const int AllowlistMismatch = 4;
    public const int LedgerConflict = 5;
    public const int GapsRemain = 10;
    public const int InvariantRegression = 20;
    public const int ConsumerRegression = 30;
}

public sealed class Verifier
{
    private readonly DbContext _db;
    private readonly List<GapRule> _rules;

    public Verifier(DbContext db, List<GapRule> rules) => (_db, _rules) = (db, rules);

    public VerifyDocument Verify(PlanDocument plan, BaselineDocument baseline,
                                 IEnumerable<Check> consumerChecks)
    {
        // Layer 1 — every row the plan actually promised to patch must no longer match.
        // Compared against the PLANNED row set, not the raw predicate count: a row
        // deliberately skipped is not a failure to close a gap.
        var remaining = new List<string>();
        using (var conn = _db.OpenTarget())
        {
            foreach (var rulePlan in plan.Rules)
            {
                var rule = _rules.First(r => r.Id == rulePlan.Id);
                var keys = rulePlan.Patches.Select(p => p.Key[rule.Key]).ToList();
                if (keys.Count == 0) continue;

                foreach (var stillBad in conn.Query<string>(GapQuery.RemainingAmong(rule, keys)))
                    remaining.Add($"{rule.Id}:{stillBad}");
            }
        }
        var l1 = remaining.Count == 0;

        // Layers 2 and 3 — run through the same CheckRunner that produced the baseline.
        var invariants = CheckRunner.Run(_db, CheckRunner.InvariantChecks(_rules));
        var consumer = CheckRunner.Run(_db, consumerChecks);

        var (invRegressions, invPreexisting) = Diff(baseline.Invariants, invariants);
        var (conRegressions, conPreexisting) = Diff(baseline.Consumer, consumer);

        var regressions = invRegressions.Concat(conRegressions).ToList();
        var preexisting = invPreexisting.Concat(conPreexisting).ToList();

        return new VerifyDocument(plan.RunId, l1,
            L2: invRegressions.Count == 0, L3: conRegressions.Count == 0,
            regressions, preexisting, invariants, consumer, remaining);
    }

    /// <summary>
    /// Green-to-red is a regression and fails the run. Red-to-red was already broken before
    /// anything changed — it is listed and never fatal, which is the whole reason a
    /// baseline is captured at scan time.
    /// </summary>
    private static (List<string> Regressions, List<string> Preexisting) Diff(
        List<CheckResult> before, List<CheckResult> after)
    {
        var wasPassing = before.Where(b => b.Passed).Select(b => b.Name)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var regressions = new List<string>();
        var preexisting = new List<string>();

        foreach (var result in after.Where(a => !a.Passed))
        {
            if (wasPassing.Contains(result.Name)) regressions.Add(result.Name);
            else preexisting.Add(result.Name);
        }
        return (regressions, preexisting);
    }

    public static int ExitCodeFor(VerifyDocument v) =>
        !v.L1 ? ExitCodes.GapsRemain
        : !v.L2 ? ExitCodes.InvariantRegression
        : !v.L3 ? ExitCodes.ConsumerRegression
        : ExitCodes.Ok;
}
