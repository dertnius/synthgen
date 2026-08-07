using System.Globalization;
using System.Text;
using Bogus;

namespace SynthGen.Core.Generation;

/// <summary>Mutable per-row state shared by all column generators of a run.</summary>
public sealed class GenerationContext
{
    public required Random Rng { get; init; }
    public required Faker Faker { get; init; }
    public int RowNumber { get; set; }
}

public interface IValueGenerator
{
    object? Next(GenerationContext ctx);
}

internal sealed class IntGenerator : IValueGenerator
{
    private readonly long _min, _max;
    public IntGenerator(long min, long max)
    {
        if (min > max) throw new GenerationException($"int range is empty: min {min} > max {max}.");
        (_min, _max) = (min, max);
    }
    public object? Next(GenerationContext ctx) =>
        _max == long.MaxValue ? ctx.Rng.NextInt64(_min, _max) : ctx.Rng.NextInt64(_min, _max + 1);
}

internal sealed class DecimalGenerator : IValueGenerator
{
    private readonly decimal _min, _max;
    public DecimalGenerator(decimal min, decimal max)
    {
        if (min > max) throw new GenerationException($"decimal range is empty: min {min} > max {max}.");
        (_min, _max) = (min, max);
    }
    public object? Next(GenerationContext ctx) => _min + (decimal)ctx.Rng.NextDouble() * (_max - _min);
}

internal sealed class BoolGenerator : IValueGenerator
{
    private readonly double _trueRate;
    public BoolGenerator(double trueRate) => _trueRate = trueRate;
    public object? Next(GenerationContext ctx) => ctx.Rng.NextDouble() < _trueRate;
}

internal sealed class DateTimeGenerator : IValueGenerator
{
    private readonly DateTime _min;
    private readonly long _units;      // whole days or whole seconds in the range
    private readonly bool _dateOnly;
    public DateTimeGenerator(DateTime min, DateTime max, bool dateOnly)
    {
        if (min > max) throw new GenerationException($"date range is empty: min {min:o} > max {max:o}.");
        _min = min;
        _dateOnly = dateOnly;
        _units = dateOnly
            ? (long)(max.Date - min.Date).TotalDays + 1
            : (long)(max - min).TotalSeconds + 1;
    }
    public object? Next(GenerationContext ctx)
    {
        var offset = ctx.Rng.NextInt64(0, _units);
        return _dateOnly ? _min.Date.AddDays(offset) : _min.AddSeconds(offset);
    }
}

internal sealed class TimeGenerator : IValueGenerator
{
    private readonly long _minSec, _maxSec;
    public TimeGenerator(TimeSpan? min, TimeSpan? max)
    {
        _minSec = (long)(min ?? TimeSpan.Zero).TotalSeconds;
        _maxSec = (long)(max ?? new TimeSpan(23, 59, 59)).TotalSeconds;
        if (_minSec > _maxSec) throw new GenerationException("time range is empty.");
    }
    public object? Next(GenerationContext ctx) =>
        TimeSpan.FromSeconds(ctx.Rng.NextInt64(_minSec, _maxSec + 1));
}

internal sealed class GuidGenerator : IValueGenerator
{
    public object? Next(GenerationContext ctx)
    {
        // Derived from the seeded RNG so runs are reproducible; shaped like a v4 GUID.
        var bytes = new byte[16];
        ctx.Rng.NextBytes(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}

internal sealed class StringGenerator : IValueGenerator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly int _length;
    public StringGenerator(int length) => _length = Math.Max(1, length);
    public object? Next(GenerationContext ctx)
    {
        var sb = new StringBuilder(_length);
        for (int i = 0; i < _length; i++)
            sb.Append(Alphabet[ctx.Rng.Next(Alphabet.Length)]);
        return sb.ToString();
    }
}

internal sealed class BytesGenerator : IValueGenerator
{
    private readonly int _length;
    public BytesGenerator(int length) => _length = Math.Max(1, length);
    public object? Next(GenerationContext ctx)
    {
        var bytes = new byte[_length];
        ctx.Rng.NextBytes(bytes);
        return bytes;
    }
}

/// <summary>Expands {row}, {guid} and {rand:min-max} tokens.</summary>
internal sealed class TemplateGenerator : IValueGenerator
{
    private readonly string _template;
    private readonly GuidGenerator _guid = new();
    public TemplateGenerator(string template) => _template = template;

    public object? Next(GenerationContext ctx)
    {
        var result = new StringBuilder(_template)
            .Replace("{row}", ctx.RowNumber.ToString(CultureInfo.InvariantCulture))
            .ToString();

        while (result.Contains("{guid}"))
        {
            int idx = result.IndexOf("{guid}", StringComparison.Ordinal);
            result = result[..idx] + _guid.Next(ctx) + result[(idx + "{guid}".Length)..];
        }

        int start;
        while ((start = result.IndexOf("{rand:", StringComparison.Ordinal)) >= 0)
        {
            int end = result.IndexOf('}', start);
            if (end < 0) break;
            var spec = result[(start + 6)..end];
            var parts = spec.Split('-', 2);
            long min = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long max = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : min;
            var value = ctx.Rng.NextInt64(min, max + 1).ToString(CultureInfo.InvariantCulture);
            result = result[..start] + value + result[(end + 1)..];
        }
        return result;
    }
}

internal sealed class PickGenerator : IValueGenerator
{
    private readonly List<string> _values;
    private readonly double[]? _cumulative;

    public PickGenerator(List<string> values, List<double>? weights)
    {
        _values = values;
        if (weights is not null)
        {
            double total = weights.Sum();
            if (total <= 0) throw new GenerationException("pick weights must sum to a positive number.");
            _cumulative = new double[weights.Count];
            double running = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                running += weights[i] / total;
                _cumulative[i] = running;
            }
        }
    }

    public object? Next(GenerationContext ctx)
    {
        if (_cumulative is null)
            return _values[ctx.Rng.Next(_values.Count)];
        double roll = ctx.Rng.NextDouble();
        for (int i = 0; i < _cumulative.Length; i++)
            if (roll <= _cumulative[i]) return _values[i];
        return _values[^1];
    }
}

internal sealed class SequenceGenerator : IValueGenerator
{
    private readonly long _start, _step;
    public SequenceGenerator(long start, long step) => (_start, _step) = (start, step);
    public object? Next(GenerationContext ctx) => _start + _step * (ctx.RowNumber - 1);
}

internal sealed class FakerGenerator : IValueGenerator
{
    private readonly Func<Faker, object> _fn;
    public FakerGenerator(string method) => _fn = FakerMap.Resolve(method);
    public object? Next(GenerationContext ctx) => _fn(ctx.Faker);
}

/// <summary>Picks randomly from values fetched up-front (query strategy, FK sources).</summary>
internal sealed class LookupGenerator : IValueGenerator
{
    private readonly IReadOnlyList<object> _values;
    public LookupGenerator(string column, IReadOnlyList<object> values)
    {
        if (values.Count == 0)
            throw new GenerationException(
                $"Column '{column}': the lookup query returned no rows. " +
                "Populate the referenced table first (generate it in a previous run).");
        _values = values;
    }
    public object? Next(GenerationContext ctx) => _values[ctx.Rng.Next(_values.Count)];
}

internal sealed class ConstantGenerator : IValueGenerator
{
    private readonly string _value;
    public ConstantGenerator(string value) => _value = value;
    public object? Next(GenerationContext ctx) => _value;
}

/// <summary>Coerces raw generated values to the column's CLR type (with truncation counting).</summary>
internal sealed class CoercingWrapper : IValueGenerator
{
    private readonly IValueGenerator _inner;
    private readonly Model.ColumnDefinition _column;
    private readonly Action<string> _onTruncate;

    public CoercingWrapper(IValueGenerator inner, Model.ColumnDefinition column, Action<string> onTruncate)
        => (_inner, _column, _onTruncate) = (inner, column, onTruncate);

    public object? Next(GenerationContext ctx) =>
        SqlTypeMapper.Coerce(_inner.Next(ctx), _column, _onTruncate);
}

/// <summary>Returns NULL for a fraction of rows, delegating otherwise.</summary>
internal sealed class NullRateWrapper : IValueGenerator
{
    private readonly IValueGenerator _inner;
    private readonly double _nullRate;
    public NullRateWrapper(IValueGenerator inner, double nullRate) => (_inner, _nullRate) = (inner, nullRate);
    public object? Next(GenerationContext ctx) =>
        ctx.Rng.NextDouble() < _nullRate ? null : _inner.Next(ctx);
}

/// <summary>Rejects duplicate values (nulls excluded) with a bounded retry loop.</summary>
internal sealed class UniqueWrapper : IValueGenerator
{
    private const int MaxAttempts = 100;
    private readonly IValueGenerator _inner;
    private readonly string _column;
    private readonly HashSet<object> _seen = new();

    public UniqueWrapper(IValueGenerator inner, string column) => (_inner, _column) = (inner, column);

    public object? Next(GenerationContext ctx)
    {
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var value = _inner.Next(ctx);
            if (value is null) return null;
            if (_seen.Add(value)) return value;
        }
        throw new GenerationException(
            $"Column '{_column}': could not find a new unique value after {MaxAttempts} attempts " +
            $"at row {ctx.RowNumber}. Widen the value domain (range, length, or source query).");
    }
}
