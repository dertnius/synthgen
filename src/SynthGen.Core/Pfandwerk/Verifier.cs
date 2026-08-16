using Dapper;

namespace Pfandwerk;

// ----------------------------------------------------------------- VERIFY

public sealed class Verifier
{
    private readonly DbContext _db;
    private readonly List<GapRule> _rules;

    public Verifier(DbContext db, List<GapRule> rules) => (_db, _rules) = (db, rules);

    public VerifyDocument Verify(PlanDocument plan, BaselineDocument baseline, IEnumerable<Check> consumerChecks)
    {
        // Layer 1 compares against the PLANNED row set, not the raw predicate count: a row
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

        var invariants = NoteUnjudgedRows(CheckRunner.Run(_db, CheckRunner.Invariants(_rules)));
        var consumer = CheckRunner.Run(_db, consumerChecks);

        var (invRegressions, invPreexisting) = Diff(baseline.Invariants, invariants);
        var (conRegressions, conPreexisting) = Diff(baseline.Consumer, consumer);

        return new VerifyDocument(plan.RunId,
            L1: remaining.Count == 0, L2: invRegressions.Count == 0, L3: conRegressions.Count == 0,
            invRegressions.Concat(conRegressions).ToList(),
            invPreexisting.Concat(conPreexisting).ToList(),
            invariants, consumer, remaining);
    }

    /// <summary>
    /// Layer 2 counts rows where <c>NOT (invariant)</c> is TRUE, which is not the same as
    /// "every row was judged": a NULL in any column the invariant references makes it
    /// UNKNOWN, and the row is neither counted nor reported. Say so on the result rather
    /// than letting a green check imply a whole table was checked.
    ///
    /// <para>The note is advisory — <c>Passed</c> is untouched, so nothing that passes today
    /// starts failing. The count is the same one PLAN prints as coverage.</para>
    /// </summary>
    private List<CheckResult> NoteUnjudgedRows(List<CheckResult> invariants)
    {
        using var conn = _db.OpenTarget();
        return invariants.Select(result =>
        {
            var rule = _rules.FirstOrDefault(r => $"{r.Id}.invariant" == result.Name);
            if (rule is null) return result;

            var (count, _) = Indeterminate.Rows(conn, rule, samples: 0);
            if (count == 0) return result;

            var note = $"{count} row(s) not evaluated: the invariant is UNKNOWN where a column " +
                       "it references is NULL";
            return result with { Detail = result.Detail is null ? note : $"{result.Detail}; {note}" };
        }).ToList();
    }

    /// <summary>
    /// Green-to-red is a regression and fails the run. Red-to-red was already broken before
    /// anything changed — it is listed and never fatal, which is the entire reason a
    /// baseline is captured at plan time.
    /// </summary>
    private static (List<string> Regressions, List<string> Preexisting) Diff(
        List<CheckResult> before, List<CheckResult> after)
    {
        var wasPassing = before.Where(b => b.Passed).Select(b => b.Name)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var regressions = new List<string>();
        var preexisting = new List<string>();
        foreach (var r in after.Where(a => !a.Passed))
            (wasPassing.Contains(r.Name) ? regressions : preexisting).Add(r.Name);
        return (regressions, preexisting);
    }

    public static int ExitCodeFor(VerifyDocument v) =>
        !v.L1 ? ExitCodes.GapsRemain
        : !v.L2 ? ExitCodes.InvariantRegression
        : !v.L3 ? ExitCodes.ConsumerRegression
        : ExitCodes.Ok;
}
