using System.Globalization;
using Bogus;
using SynthGen.Core.Model;
using SynthGen.Core.Rules;

namespace SynthGen.Core.Generation;

/// <summary>
/// Produces rows for a generation plan. Values come out coerced to the column's CLR
/// type, in the order of <see cref="GenerationPlan.Columns"/>.
/// </summary>
public sealed class RowGenerator
{
    private readonly GenerationPlan _plan;
    private readonly ValueGenerator[] _generators;
    private readonly GenerationContext _context;
    private readonly Dictionary<string, int> _truncations = new(StringComparer.OrdinalIgnoreCase);

    public int EffectiveSeed { get; }
    public int RowCount => _plan.Rules.Rows;
    public int BatchSize => _plan.Rules.BatchSize;
    public bool KeepIdentity => _plan.KeepIdentity;
    public string TableName => _plan.Table.QualifiedName;
    public IReadOnlyList<PlannedColumn> Columns => _plan.Columns;
    /// <summary>Column name -> number of values truncated to the DDL length.</summary>
    public IReadOnlyDictionary<string, int> Truncations => _truncations;

    public RowGenerator(
        GenerationPlan plan,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? lookupData = null)
    {
        _plan = plan;
        EffectiveSeed = plan.Rules.Seed ?? Random.Shared.Next(int.MaxValue);
        _context = new GenerationContext
        {
            Faker = new Faker("en") { Random = new Randomizer(EffectiveSeed) },
        };

        _generators = new ValueGenerator[plan.Columns.Count];
        for (int i = 0; i < plan.Columns.Count; i++)
            _generators[i] = BuildChain(plan.Columns[i], lookupData);
    }

    public IEnumerable<object?[]> Rows()
    {
        for (int row = 1; row <= RowCount; row++)
        {
            _context.RowNumber = row;
            var values = new object?[_generators.Length];
            for (int i = 0; i < _generators.Length; i++)
                values[i] = _generators[i](_context);
            yield return values;
        }
    }

    private void CountTruncation(string column) =>
        _truncations[column] = _truncations.GetValueOrDefault(column) + 1;

    private ValueGenerator BuildChain(
        PlannedColumn planned,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? lookupData)
    {
        var generator = BuildBase(planned, lookupData);

        // Coerce (incl. string truncation to DDL length) BEFORE the unique check, so
        // uniqueness is enforced on the value that actually reaches the database.
        generator = ValueGenerators.Coercing(generator, planned.Column, CountTruncation);

        if (planned.Rule.Unique == true)
            generator = ValueGenerators.Unique(generator, planned.Column.Name);
        if (planned.Rule.NullRate is > 0)
            generator = ValueGenerators.WithNullRate(generator, planned.Rule.NullRate.Value);

        return generator;
    }

    private static ValueGenerator BuildBase(
        PlannedColumn planned,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? lookupData)
    {
        var col = planned.Column;
        var rule = planned.Rule;

        return rule.Strategy switch
        {
            "int" => ValueGenerators.Int(
                ParseLong(rule.Min, col, 0),
                ParseLong(rule.Max, col, 1_000_000)),
            "decimal" => ValueGenerators.Decimal(
                ParseDecimal(rule.Min, col, 0),
                ParseDecimal(rule.Max, col, 10_000)),
            "bool" => ValueGenerators.Bool(rule.TrueRate ?? 0.5),
            "date" => ValueGenerators.Date(
                ParseDate(rule.Min, col, new DateTime(2020, 1, 1)),
                ParseDate(rule.Max, col, new DateTime(2025, 12, 31)),
                dateOnly: true),
            "datetime" => ValueGenerators.Date(
                ParseDate(rule.Min, col, new DateTime(2020, 1, 1)),
                ParseDate(rule.Max, col, new DateTime(2025, 12, 31)),
                dateOnly: false),
            "time" => ValueGenerators.Time(ParseTime(rule.Min, col), ParseTime(rule.Max, col)),
            "guid" => ValueGenerators.Guid(),
            "string" => ValueGenerators.String(rule.Length ?? DefaultStringLength(col)),
            "bytes" => ValueGenerators.Bytes(rule.Length ?? DefaultBytesLength(col)),
            "template" => ValueGenerators.Template(rule.Template!),
            "pick" => ValueGenerators.Pick(rule.Values!, rule.Weights),
            "sequence" => ValueGenerators.Sequence(rule.Start ?? 1, rule.Step ?? 1),
            "faker" => ValueGenerators.FakerMethod(rule.Method!),
            "constant" => ValueGenerators.Constant(rule.Value!),
            "query" => ValueGenerators.Lookup(
                col.Name,
                lookupData?.GetValueOrDefault(col.Name)
                    ?? throw new GenerationException(
                        $"Column '{col.Name}' uses the query strategy but no lookup data was " +
                        "fetched. A database connection is required for query-based columns.")),
            _ => throw new GenerationException(
                $"Column '{col.Name}': unknown strategy '{rule.Strategy}'."),
        };
    }

    private static int DefaultStringLength(ColumnDefinition col) =>
        col.Length is > 0 ? Math.Min(col.Length.Value, 20) : 20;

    private static int DefaultBytesLength(ColumnDefinition col) =>
        col.Length is > 0 ? Math.Min(col.Length.Value, 16) : 16;

    private static long ParseLong(string? text, ColumnDefinition col, long fallback)
    {
        if (text is null) return fallback;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new GenerationException($"Column '{col.Name}': '{text}' is not a valid integer bound.");
    }

    private static decimal ParseDecimal(string? text, ColumnDefinition col, decimal fallback)
    {
        if (text is null) return fallback;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new GenerationException($"Column '{col.Name}': '{text}' is not a valid decimal bound.");
    }

    private static DateTime ParseDate(string? text, ColumnDefinition col, DateTime fallback)
    {
        if (text is null) return fallback;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return value;
        throw new GenerationException($"Column '{col.Name}': '{text}' is not a valid date bound.");
    }

    private static TimeSpan? ParseTime(string? text, ColumnDefinition col)
    {
        if (text is null) return null;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new GenerationException($"Column '{col.Name}': '{text}' is not a valid time bound.");
    }
}
