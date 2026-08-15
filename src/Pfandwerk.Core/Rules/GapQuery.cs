namespace Pfandwerk.Core.Rules;

/// <summary>
/// The single place a gap predicate becomes SQL. SCAN, PLAN and VERIFY layer 1 all call
/// this — three independent implementations would eventually disagree about what a gap is,
/// and the symptom would be a run reporting success while gaps remain open.
/// </summary>
public static class GapQuery
{
    public static string Count(GapRule rule) =>
        $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.Gap}";

    /// <summary>Key, current value, and any declared inputs, for every matching row.</summary>
    public static string Rows(GapRule rule)
    {
        var columns = new List<string> { rule.Key, rule.Column };
        if (rule.Inputs is not null) columns.AddRange(rule.Inputs);
        var distinct = columns.Distinct(StringComparer.OrdinalIgnoreCase);
        return $"SELECT {string.Join(", ", distinct)} FROM {rule.Table} WHERE {rule.Gap} ORDER BY {rule.Key}";
    }

    /// <summary>Rows still matching the gap among a specific planned key set (VERIFY layer 1).</summary>
    public static string RemainingAmong(GapRule rule, IEnumerable<string> keys)
    {
        var list = string.Join(", ", keys.Select(Literal));
        return $"SELECT {rule.Key} FROM {rule.Table} WHERE ({rule.Gap}) AND {rule.Key} IN ({list})";
    }

    /// <summary>Rows violating the rule's invariant, across the whole table.</summary>
    public static string InvariantViolations(GapRule rule) =>
        $"SELECT {rule.Key} FROM {rule.Table} WHERE NOT ({rule.Invariant}) ORDER BY {rule.Key}";

    public static string Update(GapRule rule, string keyParam, string valueParam) =>
        $"UPDATE {rule.Table} SET {rule.Column} = {valueParam} WHERE {rule.Key} = {keyParam}";

    /// <summary>Keys arrive as strings from artifacts; numeric ones must not be quoted.</summary>
    private static string Literal(string key) =>
        long.TryParse(key, out _) ? key : "'" + key.Replace("'", "''") + "'";
}
