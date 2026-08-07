using YamlDotNet.Serialization;

namespace SynthGen.Core.Rules;

/// <summary>Root of the rules YAML document.</summary>
public sealed class RulesFile
{
    /// <summary>Target table, e.g. "dbo.Customers". Optional when the DDL has a single table.</summary>
    public string? Table { get; set; }

    /// <summary>Number of rows to generate.</summary>
    public int Rows { get; set; } = 100;

    /// <summary>Seed for deterministic output. Omit for a random run.</summary>
    public int? Seed { get; set; }

    /// <summary>When true, existing rows are deleted before loading.</summary>
    public bool TruncateBeforeLoad { get; set; }

    /// <summary>SqlBulkCopy batch size.</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>Per-column generation rules, keyed by column name.</summary>
    public Dictionary<string, ColumnRule> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Validation queries executed after the data is loaded.</summary>
    public List<Evaluation> Evaluations { get; set; } = new();
}

/// <summary>
/// Generation rule for one column. All members are optional; anything not set is
/// inferred from the DDL (type, length, nullability, identity, FK).
/// </summary>
public sealed class ColumnRule
{
    /// <summary>
    /// One of: auto, skip, dbDefault, int, decimal, bool, date, datetime, time, guid,
    /// string, template, pick, sequence, faker, query, constant.
    /// </summary>
    public string? Strategy { get; set; }

    /// <summary>Lower bound (numbers, dates). Parsed according to the column type.</summary>
    public string? Min { get; set; }

    /// <summary>Upper bound (numbers, dates), inclusive.</summary>
    public string? Max { get; set; }

    /// <summary>Values for the "pick" strategy.</summary>
    public List<string>? Values { get; set; }

    /// <summary>Optional weights matching <see cref="Values"/> (relative, need not sum to 1).</summary>
    public List<double>? Weights { get; set; }

    /// <summary>Template for the "template" strategy. Tokens: {row}, {guid}, {rand:min-max}.</summary>
    public string? Template { get; set; }

    /// <summary>Bogus method for the "faker" strategy, e.g. "internet.email".</summary>
    public string? Method { get; set; }

    /// <summary>SQL that yields candidate values for the "query" strategy (FK sources).</summary>
    public string? Query { get; set; }

    /// <summary>Fixed value for the "constant" strategy.</summary>
    public string? Value { get; set; }

    /// <summary>Fraction of rows (0..1) that receive NULL. Only honored for nullable columns.</summary>
    public double? NullRate { get; set; }

    /// <summary>Enforce distinct values across the generated set.</summary>
    public bool? Unique { get; set; }

    /// <summary>Start value for the "sequence" strategy.</summary>
    public long? Start { get; set; }

    /// <summary>Step for the "sequence" strategy.</summary>
    public long? Step { get; set; }

    /// <summary>Probability of true (0..1) for the "bool" strategy.</summary>
    public double? TrueRate { get; set; }

    /// <summary>Generated string length cap; defaults to the DDL length.</summary>
    public int? Length { get; set; }
}

/// <summary>A validation query executed after generation, with an expected scalar outcome.</summary>
public sealed class Evaluation
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public Expectation? Expect { get; set; }
}

/// <summary>Expected outcome for an evaluation's scalar result. Set exactly one condition.</summary>
public sealed class Expectation
{
    [YamlMember(Alias = "equals")]
    public string? EqualsValue { get; set; }

    /// <summary>Numeric lower bound (inclusive).</summary>
    public string? Min { get; set; }

    /// <summary>Numeric upper bound (inclusive).</summary>
    public string? Max { get; set; }

    /// <summary>Two-element [low, high] inclusive numeric range.</summary>
    public List<string>? Between { get; set; }

    /// <summary>Absolute tolerance applied to "equals" for numeric results.</summary>
    public double? Tolerance { get; set; }
}
