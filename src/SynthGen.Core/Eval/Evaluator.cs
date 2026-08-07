using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using SynthGen.Core.Rules;

namespace SynthGen.Core.Eval;

public sealed class EvaluationResult
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public object? Value { get; init; }
    public string? Expected { get; init; }
    public bool Passed { get; init; }
    public string? Error { get; init; }
    /// <summary>True when the evaluation has no expectation and only reports its value.</summary>
    public bool Informational { get; init; }
}

/// <summary>
/// Runs the rules file's validation queries via Dapper and compares each scalar
/// result with its expectation.
/// </summary>
public sealed class Evaluator
{
    private readonly Func<IDbConnection> _connectionFactory;

    /// <summary>SQL Server convenience constructor.</summary>
    public Evaluator(string connectionString)
        : this(() => new SqlConnection(connectionString)) { }

    /// <summary>Provider-agnostic constructor — tests pass a SQLite (or fake) factory.</summary>
    public Evaluator(Func<IDbConnection> connectionFactory) => _connectionFactory = connectionFactory;

    public List<EvaluationResult> Run(IEnumerable<Evaluation> evaluations)
    {
        using var conn = _connectionFactory();
        conn.Open();

        var results = new List<EvaluationResult>();
        foreach (var eval in evaluations)
        {
            object? value;
            try
            {
                value = conn.ExecuteScalar<object>(eval.Query, commandTimeout: 300);
            }
            catch (DbException ex)
            {
                results.Add(new EvaluationResult
                {
                    Name = eval.Name,
                    Query = eval.Query,
                    Passed = false,
                    Expected = Describe(eval.Expect),
                    Error = $"query failed: {ex.Message}",
                });
                continue;
            }

            results.Add(Check(eval, value));
        }
        return results;
    }

    /// <summary>Pure comparison logic, separated so it is unit-testable without a database.</summary>
    public static EvaluationResult Check(Evaluation eval, object? value)
    {
        var expect = eval.Expect;
        if (expect is null)
        {
            return new EvaluationResult
            {
                Name = eval.Name,
                Query = eval.Query,
                Value = value,
                Passed = true,
                Informational = true,
            };
        }

        bool passed;
        string expected = Describe(expect)!;
        try
        {
            passed = Matches(expect, value);
        }
        catch (FormatException ex)
        {
            return new EvaluationResult
            {
                Name = eval.Name,
                Query = eval.Query,
                Value = value,
                Expected = expected,
                Passed = false,
                Error = ex.Message,
            };
        }

        return new EvaluationResult
        {
            Name = eval.Name,
            Query = eval.Query,
            Value = value,
            Expected = expected,
            Passed = passed,
        };
    }

    private static bool Matches(Expectation expect, object? value)
    {
        if (expect.EqualsValue is not null)
        {
            if (value is null) return string.Equals(expect.EqualsValue, "null", StringComparison.OrdinalIgnoreCase);

            if (TryToDecimal(value, out var actual) &&
                decimal.TryParse(expect.EqualsValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var target))
            {
                var tolerance = (decimal)(expect.Tolerance ?? 0);
                return Math.Abs(actual - target) <= tolerance;
            }

            return string.Equals(
                Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim(),
                expect.EqualsValue.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (expect.Between is not null)
        {
            var actual = RequireNumeric(value);
            return actual >= ParseBound(expect.Between[0]) && actual <= ParseBound(expect.Between[1]);
        }

        if (expect.Min is not null || expect.Max is not null)
        {
            var actual = RequireNumeric(value);
            if (expect.Min is not null && actual < ParseBound(expect.Min)) return false;
            if (expect.Max is not null && actual > ParseBound(expect.Max)) return false;
            return true;
        }

        // An Expectation object with no condition set: treat as informational pass.
        return true;
    }

    private static decimal RequireNumeric(object? value)
    {
        if (value is null)
            throw new FormatException("query returned NULL but the expectation is numeric");
        if (TryToDecimal(value, out var d)) return d;
        throw new FormatException(
            $"query returned non-numeric value '{value}' but the expectation is numeric");
    }

    private static bool TryToDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte or short or int or long or float or double or decimal:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                return true;
            case string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static decimal ParseBound(string text)
    {
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;
        throw new FormatException($"'{text}' is not a valid numeric bound");
    }

    private static string? Describe(Expectation? expect)
    {
        if (expect is null) return null;
        if (expect.EqualsValue is not null)
            return expect.Tolerance is > 0
                ? $"= {expect.EqualsValue} (±{expect.Tolerance})"
                : $"= {expect.EqualsValue}";
        if (expect.Between is not null) return $"in [{expect.Between[0]}, {expect.Between[1]}]";
        if (expect.Min is not null && expect.Max is not null) return $">= {expect.Min} and <= {expect.Max}";
        if (expect.Min is not null) return $">= {expect.Min}";
        if (expect.Max is not null) return $"<= {expect.Max}";
        return "(no condition)";
    }
}
