using System.Globalization;
using System.Text.RegularExpressions;
using Bogus;

namespace SynthGen.Core.Generation;

/// <summary>Mutable per-row state shared by all column generators of a run.</summary>
public sealed class GenerationContext
{
    public required Faker Faker { get; init; }
    public int RowNumber { get; set; }
}

/// <summary>A column's value source. Wrappers compose as ordinary functions.</summary>
public delegate object? ValueGenerator(GenerationContext ctx);

/// <summary>
/// Factories for every strategy's value source. All randomness draws from the run's
/// single seeded Bogus <see cref="Randomizer"/> stream, so one seed reproduces one
/// dataset — including the faker strategy, which shares the same stream.
/// </summary>
public static class ValueGenerators
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static ValueGenerator Int(long min, long max)
    {
        if (min > max) throw new GenerationException($"int range is empty: min {min} > max {max}.");
        return ctx => ctx.Faker.Random.Long(min, max);
    }

    public static ValueGenerator Decimal(decimal min, decimal max)
    {
        if (min > max) throw new GenerationException($"decimal range is empty: min {min} > max {max}.");
        return ctx => ctx.Faker.Random.Decimal(min, max);
    }

    public static ValueGenerator Bool(double trueRate) =>
        ctx => ctx.Faker.Random.Double() < trueRate;

    /// <summary>Whole days (date) or whole seconds (datetime) within the inclusive range.</summary>
    public static ValueGenerator Date(DateTime min, DateTime max, bool dateOnly)
    {
        if (min > max) throw new GenerationException($"date range is empty: min {min:o} > max {max:o}.");
        var units = dateOnly ? (long)(max.Date - min.Date).TotalDays : (long)(max - min).TotalSeconds;
        return ctx =>
        {
            var offset = ctx.Faker.Random.Long(0, units);
            return dateOnly ? min.Date.AddDays(offset) : min.AddSeconds(offset);
        };
    }

    public static ValueGenerator Time(TimeSpan? min, TimeSpan? max)
    {
        var lo = (long)(min ?? TimeSpan.Zero).TotalSeconds;
        var hi = (long)(max ?? new TimeSpan(23, 59, 59)).TotalSeconds;
        if (lo > hi) throw new GenerationException("time range is empty.");
        return ctx => TimeSpan.FromSeconds(ctx.Faker.Random.Long(lo, hi));
    }

    /// <summary>Seeded, reproducible v4 GUIDs.</summary>
    public static ValueGenerator Guid() => ctx => ctx.Faker.Random.Guid();

    public static ValueGenerator String(int length) =>
        ctx => ctx.Faker.Random.String2(Math.Max(1, length), Alphabet);

    public static ValueGenerator Bytes(int length) =>
        ctx => ctx.Faker.Random.Bytes(Math.Max(1, length));

    private static readonly Regex Token =
        new(@"\{row\}|\{guid\}|\{rand:(?<lo>\d+)(?:-(?<hi>\d+))?\}", RegexOptions.Compiled);

    /// <summary>Expands {row}, {guid} and {rand:min-max} tokens.</summary>
    public static ValueGenerator Template(string template) => ctx =>
        Token.Replace(template, m => m.Value switch
        {
            "{row}" => ctx.RowNumber.ToString(CultureInfo.InvariantCulture),
            "{guid}" => ctx.Faker.Random.Guid().ToString(),
            _ => RandToken(ctx, m),
        });

    private static string RandToken(GenerationContext ctx, Match m)
    {
        long lo = long.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture);
        long hi = m.Groups["hi"].Success ? long.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture) : lo;
        return ctx.Faker.Random.Long(lo, hi).ToString(CultureInfo.InvariantCulture);
    }

    public static ValueGenerator Pick(List<string> values, List<double>? weights)
    {
        if (weights is null)
        {
            var plain = values.ToArray();
            return ctx => ctx.Faker.Random.ArrayElement(plain);
        }

        var total = weights.Sum();
        if (total <= 0) throw new GenerationException("pick weights must sum to a positive number.");
        var items = values.ToArray();
        var normalized = weights.Select(w => (float)(w / total)).ToArray();
        return ctx => ctx.Faker.Random.WeightedRandom(items, normalized);
    }

    public static ValueGenerator Sequence(long start, long step) =>
        ctx => start + step * (ctx.RowNumber - 1);

    public static ValueGenerator FakerMethod(string method)
    {
        var fn = FakerMap.Resolve(method);
        return ctx => fn(ctx.Faker);
    }

    /// <summary>Picks randomly from values fetched up-front (query strategy, FK sources).</summary>
    public static ValueGenerator Lookup(string column, IReadOnlyList<object> values)
    {
        if (values.Count == 0)
            throw new GenerationException(
                $"Column '{column}': the lookup query returned no rows. " +
                "Populate the referenced table first (generate it in a previous run).");
        return ctx => values[ctx.Faker.Random.Int(0, values.Count - 1)];
    }

    public static ValueGenerator Constant(string value) => ctx => value;

    // ------------------------------------------------------------------ wrappers

    /// <summary>Coerces raw generated values to the column's CLR type (with truncation counting).</summary>
    public static ValueGenerator Coercing(ValueGenerator inner, Model.ColumnDefinition column,
                                          Action<string> onTruncate) =>
        ctx => SqlTypeMapper.Coerce(inner(ctx), column, onTruncate);

    /// <summary>Returns NULL for a fraction of rows, delegating otherwise.</summary>
    public static ValueGenerator WithNullRate(ValueGenerator inner, double nullRate) =>
        ctx => ctx.Faker.Random.Double() < nullRate ? null : inner(ctx);

    /// <summary>Rejects duplicate values (nulls excluded) with a bounded retry loop.</summary>
    public static ValueGenerator Unique(ValueGenerator inner, string column)
    {
        const int maxAttempts = 100;
        var seen = new HashSet<object>();
        return ctx =>
        {
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var value = inner(ctx);
                if (value is null) return null;
                if (seen.Add(value)) return value;
            }
            throw new GenerationException(
                $"Column '{column}': could not find a new unique value after {maxAttempts} attempts " +
                $"at row {ctx.RowNumber}. Widen the value domain (range, length, or source query).");
        };
    }
}
