using System.Globalization;
using SynthGen.Core.Model;

namespace SynthGen.Core.Generation;

/// <summary>Maps SQL Server column types to CLR types and coerces generated values into them.</summary>
public static class SqlTypeMapper
{
    public static Type ClrType(ColumnDefinition col) => col.SqlType switch
    {
        "bit" => typeof(bool),
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),
        "decimal" or "numeric" or "money" or "smallmoney" => typeof(decimal),
        "float" => typeof(double),
        "real" => typeof(float),
        "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "time" => typeof(TimeSpan),
        "uniqueidentifier" => typeof(Guid),
        "binary" or "varbinary" or "image" or "rowversion" or "timestamp" => typeof(byte[]),
        _ => typeof(string),
    };

    /// <summary>
    /// Converts a generated value to the column's CLR type. Strings are truncated to the
    /// declared length (counted via <paramref name="onTruncate"/>), decimals rounded to scale.
    /// </summary>
    public static object? Coerce(object? value, ColumnDefinition col, Action<string>? onTruncate = null)
    {
        if (value is null) return null;
        var target = ClrType(col);

        if (target == typeof(string))
        {
            var s = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            if (col.Length is > 0 && s.Length > col.Length.Value)
            {
                onTruncate?.Invoke(col.Name);
                s = s[..col.Length.Value];
            }
            return s;
        }

        if (target == typeof(decimal))
        {
            var d = ToDecimal(value, col);
            if (col.Scale is not null)
                d = Math.Round(d, col.Scale.Value, MidpointRounding.AwayFromZero);
            return d;
        }

        if (value.GetType() == target) return value;

        if (target == typeof(DateTime))
            return value switch
            {
                DateTimeOffset dto => dto.DateTime,
                string s => DateTime.Parse(s, CultureInfo.InvariantCulture),
                _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
            };

        if (target == typeof(DateTimeOffset))
            return value switch
            {
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero),
                string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture),
                _ => throw Mismatch(value, col),
            };

        if (target == typeof(TimeSpan))
            return value switch
            {
                string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
                _ => throw Mismatch(value, col),
            };

        if (target == typeof(Guid))
            return value switch
            {
                string s => Guid.Parse(s),
                _ => throw Mismatch(value, col),
            };

        if (target == typeof(bool))
            return value switch
            {
                string s => bool.Parse(s),
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            };

        if (target == typeof(byte[]))
            return value switch
            {
                byte[] b => b,
                string s => Convert.FromHexString(s.StartsWith("0x") ? s[2..] : s),
                _ => throw Mismatch(value, col),
            };

        try
        {
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            throw new GenerationException(
                $"Column '{col.Name}' ({col.SqlType}): cannot convert generated value " +
                $"'{value}' ({value.GetType().Name}) to {target.Name}. {ex.Message}");
        }
    }

    private static decimal ToDecimal(object value, ColumnDefinition col) => value switch
    {
        decimal d => d,
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    private static GenerationException Mismatch(object value, ColumnDefinition col) =>
        new($"Column '{col.Name}' ({col.SqlType}): generated value of type " +
            $"{value.GetType().Name} is not compatible.");
}

public sealed class GenerationException : Exception
{
    public GenerationException(string message) : base(message) { }
}
