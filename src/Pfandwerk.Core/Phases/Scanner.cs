using System.Data;
using Dapper;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Rules;

namespace Pfandwerk.Core.Phases;

/// <summary>
/// A SQL assertion that must hold. Used for both the invariant layer (generated from each
/// rule's `invariant`) and the consumer layer (declared in consumer-checks.yaml), so the
/// baseline and the post-run verification are produced by one code path — two
/// implementations would guarantee phantom regressions.
/// </summary>
public sealed record Check(string Name, string Query, int Expected);

public static class CheckRunner
{
    public static List<CheckResult> Run(DbContext db, IEnumerable<Check> checks)
    {
        using var conn = db.OpenTarget();
        var results = new List<CheckResult>();
        foreach (var check in checks)
        {
            try
            {
                var actual = conn.ExecuteScalar<int>(check.Query);
                results.Add(new CheckResult(check.Name, actual == check.Expected,
                    actual == check.Expected ? null : $"expected {check.Expected}, got {actual}"));
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult(check.Name, false, $"query failed: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>One invariant check per rule that declares one: zero violating rows.</summary>
    public static List<Check> InvariantChecks(IEnumerable<GapRule> rules) =>
        rules.Where(r => !string.IsNullOrWhiteSpace(r.Invariant))
             .Select(r => new Check($"{r.Id}.invariant",
                 $"SELECT COUNT(*) FROM ({GapQuery.InvariantViolations(r)}) v", 0))
             .ToList();
}

public sealed class Scanner
{
    private readonly DbContext _db;
    private readonly List<GapRule> _rules;

    public Scanner(DbContext db, List<GapRule> rules) => (_db, _rules) = (db, rules);

    /// <summary>
    /// Validates rules against the live schema, then counts gaps. A rule naming a dropped
    /// column is a config error surfaced here, not a runtime failure three phases later.
    /// </summary>
    public GapsDocument Scan(string runId, string rulesSha)
    {
        using var conn = _db.OpenTarget();
        var ruleGaps = new List<RuleGaps>();

        foreach (var rule in _rules)
        {
            ValidateAgainstSchema(conn, rule);

            var count = conn.ExecuteScalar<int>(GapQuery.Count(rule));
            var rows = new List<GapRow>();
            foreach (IDictionary<string, object?> row in conn.Query(GapQuery.Rows(rule)))
            {
                var key = new Dictionary<string, string>
                    { [rule.Key] = Canonical.Format(row[rule.Key]) };
                var inputs = rule.Inputs?.ToDictionary(
                    i => i, i => row[i] is null ? null : Canonical.Format(row[i]));
                rows.Add(new GapRow(key, row[rule.Column] is null ? null : Canonical.Format(row[rule.Column]), inputs));
            }
            ruleGaps.Add(new RuleGaps(rule.Id, rule.Table, rule.Column, rule.Kind,
                                      count, rule.Threshold, rows));
        }

        return new GapsDocument(runId, rulesSha, ruleGaps);
    }

    private static void ValidateAgainstSchema(IDbConnection conn, GapRule rule)
    {
        var columns = new List<string> { rule.Key, rule.Column };
        if (rule.Inputs is not null) columns.AddRange(rule.Inputs);
        try
        {
            // Selecting zero rows still fails if a column does not exist, and costs nothing.
            conn.ExecuteScalar($"SELECT {string.Join(", ", columns.Distinct())} FROM {rule.Table} WHERE 1 = 0");
        }
        catch (Exception ex)
        {
            throw new GapRulesLoadException(
                $"Rule '{rule.Id}' does not match the live schema of {rule.Table}: {ex.Message}");
        }
    }
}
