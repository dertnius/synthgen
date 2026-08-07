using SynthGen.Core.Model;
using SynthGen.Core.Rules;

namespace SynthGen.Core.Generation;

/// <summary>A column that will receive generated values, with its effective rule.</summary>
public sealed class PlannedColumn
{
    public required ColumnDefinition Column { get; init; }
    public required ColumnRule Rule { get; init; }
    public string Strategy => Rule.Strategy ?? "auto";
}

/// <summary>
/// The resolved shape of a generation run: which columns get values, which SQL lookups
/// must be fetched first, and whether identity values are being supplied explicitly.
/// </summary>
public sealed class GenerationPlan
{
    public required TableDefinition Table { get; init; }
    public required RulesFile Rules { get; init; }
    public List<PlannedColumn> Columns { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool KeepIdentity { get; set; }

    /// <summary>Column name -> SQL for "query" strategy columns; results feed the generators.</summary>
    public Dictionary<string, string> Lookups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves rules against the DDL: every column gets an effective rule (explicit or
    /// inferred), db-generated columns are excluded, and misconfigurations are surfaced.
    /// </summary>
    public static GenerationPlan Build(TableDefinition table, RulesFile rules)
    {
        var plan = new GenerationPlan { Table = table, Rules = rules };

        var unknown = rules.Columns.Keys
            .Where(name => table.FindColumn(name) is null)
            .ToList();
        if (unknown.Count > 0)
            throw new GenerationException(
                $"Rules reference columns that are not in the DDL for {table.Schema}.{table.Name}: " +
                string.Join(", ", unknown));

        foreach (var col in table.Columns)
        {
            rules.Columns.TryGetValue(col.Name, out var explicitRule);

            if (col.IsDbGenerated)
            {
                if (explicitRule is not null && explicitRule.Strategy is not "skip")
                    plan.Warnings.Add(
                        $"Column '{col.Name}' is {(col.IsComputed ? "computed" : "rowversion")}; " +
                        "its rule is ignored because the database generates the value.");
                continue;
            }

            var rule = explicitRule is null || explicitRule.Strategy is null or "auto"
                ? MergeInferred(explicitRule, ColumnInference.Infer(col, table))
                : explicitRule;

            if (col.IsIdentity)
            {
                if (rule.Strategy is "skip" or "dbDefault") continue;
                plan.KeepIdentity = true;
            }

            switch (rule.Strategy)
            {
                case "skip" or "dbDefault":
                    if (!col.IsNullable && col.DefaultExpression is null && !col.IsIdentity)
                        plan.Warnings.Add(
                            $"Column '{col.Name}' is NOT NULL with no DEFAULT but its strategy is " +
                            $"'{rule.Strategy}' — the insert will likely fail.");
                    continue;
                case "query":
                    plan.Lookups[col.Name] = rule.Query!;
                    break;
            }

            if (rule.NullRate is > 0 && !col.IsNullable)
            {
                plan.Warnings.Add(
                    $"Column '{col.Name}' is NOT NULL; nullRate {rule.NullRate} is ignored.");
                rule.NullRate = 0;
            }

            plan.Columns.Add(new PlannedColumn { Column = col, Rule = rule });
        }

        if (plan.Columns.Count == 0)
            throw new GenerationException("No columns left to generate — every column is skipped or db-generated.");

        return plan;
    }

    /// <summary>
    /// A rule without a strategy still contributes modifiers (nullRate, unique, ...)
    /// on top of the inferred strategy.
    /// </summary>
    private static ColumnRule MergeInferred(ColumnRule? partial, ColumnRule inferred)
    {
        if (partial is null) return inferred;
        inferred.NullRate = partial.NullRate ?? inferred.NullRate;
        inferred.Unique = partial.Unique ?? inferred.Unique;
        inferred.Min = partial.Min ?? inferred.Min;
        inferred.Max = partial.Max ?? inferred.Max;
        inferred.Length = partial.Length ?? inferred.Length;
        return inferred;
    }
}
