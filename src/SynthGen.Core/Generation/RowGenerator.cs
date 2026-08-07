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
    private readonly IValueGenerator[] _generators;
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
            Rng = new Random(EffectiveSeed),
            Faker = new Faker("en") { Random = new Randomizer(EffectiveSeed) },
        };

        _generators = new IValueGenerator[plan.Columns.Count];
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
                values[i] = _generators[i].Next(_context);
            yield return values;
        }
    }

    private void CountTruncation(string column) =>
        _truncations[column] = _truncations.GetValueOrDefault(column) + 1;

    private IValueGenerator BuildChain(
        PlannedColumn planned,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? lookupData)
    {
        var generator = BuildBase(planned, lookupData);

        // Coerce (incl. string truncation to DDL length) BEFORE the unique check, so
        // uniqueness is enforced on the value that actually reaches the database.
        generator = new CoercingWrapper(generator, planned.Column, CountTruncation);

        if (planned.Rule.Unique == true)
            generator = new UniqueWrapper(generator, planned.Column.Name);
        if (planned.Rule.NullRate is > 0)
            generator = new NullRateWrapper(generator, planned.Rule.NullRate.Value);

        return generator;
    }

    private static IValueGenerator BuildBase(
        PlannedColumn planned,
        IReadOnlyDictionary<string, IReadOnlyList<object>>? lookupData)
    {
        var col = planned.Column;
        var rule = planned.Rule;

        return rule.Strategy switch
        {
            "int" => new IntGenerator(
                ParseLong(rule.Min, col, 0),
                ParseLong(rule.Max, col, 1_000_000)),
            "decimal" => new DecimalGenerator(
                ParseDecimal(rule.Min, col, 0),
                ParseDecimal(rule.Max, col, 10_000)),
            "bool" => new BoolGenerator(rule.TrueRate ?? 0.5),
            "date" => new DateTimeGenerator(
                ParseDate(rule.Min, col, new DateTime(2020, 1, 1)),
                ParseDate(rule.Max, col, new DateTime(2025, 12, 31)),
                dateOnly: true),
            "datetime" => new DateTimeGenerator(
                ParseDate(rule.Min, col, new DateTime(2020, 1, 1)),
                ParseDate(rule.Max, col, new DateTime(2025, 12, 31)),
                dateOnly: false),
            "time" => new TimeGenerator(ParseTime(rule.Min, col), ParseTime(rule.Max, col)),
            "guid" => new GuidGenerator(),
            "string" => new StringGenerator(rule.Length ?? DefaultStringLength(col)),
            "bytes" => new BytesGenerator(rule.Length ?? DefaultBytesLength(col)),
            "template" => new TemplateGenerator(rule.Template!),
            "pick" => new PickGenerator(rule.Values!, rule.Weights),
            "sequence" => new SequenceGenerator(rule.Start ?? 1, rule.Step ?? 1),
            "faker" => new FakerGenerator(rule.Method!),
            "constant" => new ConstantGenerator(rule.Value!),
            "query" => new LookupGenerator(
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
