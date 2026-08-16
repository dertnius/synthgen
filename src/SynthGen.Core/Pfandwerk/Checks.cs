using System.Data;
using System.Globalization;
using Dapper;
using SynthGen.Core.Eval;

namespace Pfandwerk;

// SCAN, PLAN, APPLY and VERIFY. Scanning and planning are one pass: the rows a rule matches
// are only ever needed in order to plan their values, so there is no artifact between them.

/// <summary>
/// A SQL assertion that must hold. Serves both the invariant layer (generated from each
/// rule's `invariant`) and the consumer layer (declared in consumer-checks.yaml), so the
/// baseline and the post-run comparison are produced by one code path — two
/// implementations would guarantee phantom regressions.
/// </summary>
public sealed record Check(string Name, string Query, int Expected);

public static class CheckRunner
{
    /// <summary>
    /// Thin adapter over <see cref="Evaluator"/> — the repo's one SQL assertion runner —
    /// that keeps the CheckResult artifact shape (and its exact detail strings, which
    /// baseline.json/verify.json diffs depend on) stable.
    /// </summary>
    public static List<CheckResult> Run(DbContext db, IEnumerable<Check> checks)
    {
        var list = checks.ToList();
        var evaluations = list.Select(c => new Evaluation
        {
            Name = c.Name,
            Query = c.Query,
            Expect = new Expectation { EqualsValue = c.Expected.ToString(CultureInfo.InvariantCulture) },
        });

        var evaluated = new Evaluator(db.OpenTarget).Run(evaluations);

        return list.Zip(evaluated, (check, r) => new CheckResult(check.Name, r.Passed,
                r.Passed ? null : r.Error ?? $"expected {check.Expected}, got " +
                    (Convert.ToString(r.Value, CultureInfo.InvariantCulture) is { Length: > 0 } v ? v : "NULL")))
            .ToList();
    }

    /// <summary>
    /// One check per rule that declares an invariant: zero violating rows.
    ///
    /// <para>Note what this cannot see. The query is <c>NOT (invariant)</c>, so a row where
    /// the invariant evaluates to UNKNOWN — a NULL in any column it references — is neither
    /// returned nor counted, and layer 2 passes it. `plan` reports that count as
    /// `coverage.indeterminate`; this check does not.</para>
    /// </summary>
    public static List<Check> Invariants(IEnumerable<GapRule> rules) =>
        rules.Where(r => !string.IsNullOrWhiteSpace(r.Invariant))
             .Select(r => new Check($"{r.Id}.invariant",
                 $"SELECT COUNT(*) FROM ({GapQuery.InvariantViolations(r)}) v", 0))
             .ToList();
}

/// <summary>
/// Rows a rule's invariant can neither confirm nor deny, because a column it references is
/// NULL on that row.
///
/// <para>There is no SQL expression for "this evaluated to UNKNOWN" — <c>NOT (X OR NOT X)</c>
/// is itself UNKNOWN. Counting the rows the predicate says TRUE for, the rows it says FALSE
/// for, and subtracting from the table is the only provider-neutral way to find them. PLAN
/// reports the count as coverage; VERIFY puts it on the layer-2 result, because
/// <c>NOT (invariant)</c> passes these rows in silence.</para>
/// </summary>
public static class Indeterminate
{
    public static (int Count, List<string> Keys) Rows(IDbConnection conn, GapRule rule, int samples)
    {
        var total = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {rule.Table}");
        var judged = conn.Query<string>(GapQuery.InvariantHolds(rule))
                         .Concat(conn.Query<string>(GapQuery.InvariantViolations(rule)))
                         .ToHashSet(StringComparer.Ordinal);

        var count = Math.Max(0, total - judged.Count);
        var keys = count == 0 || samples == 0
            ? new List<string>()
            : conn.Query<string>($"SELECT {rule.Key} FROM {rule.Table} ORDER BY {rule.Key}")
                  .Where(k => !judged.Contains(k)).Take(samples).ToList();
        return (count, keys);
    }
}
