using SynthGen.Core.Eval;
using SynthGen.Core.Rules;

namespace SynthGen.Tests;

public class EvaluatorTests
{
    private static Evaluation Eval(string? equals = null, string? min = null, string? max = null,
        List<string>? between = null, double? tolerance = null, bool noExpect = false) => new()
    {
        Name = "t",
        Query = "SELECT 1",
        Expect = noExpect ? null : new Expectation
        {
            EqualsValue = equals,
            Min = min,
            Max = max,
            Between = between,
            Tolerance = tolerance,
        },
    };

    [Theory]
    [InlineData("1000", 1000, true)]
    [InlineData("1000", 999, false)]
    [InlineData("0", 0, true)]
    public void Equals_compares_numerically(string expected, int actual, bool pass)
    {
        Assert.Equal(pass, Evaluator.Check(Eval(equals: expected), actual).Passed);
    }

    [Fact]
    public void Equals_handles_sql_numeric_types()
    {
        // COUNT(*) comes back as int, SUM(bigint) as long, AVG(decimal) as decimal.
        Assert.True(Evaluator.Check(Eval(equals: "5"), 5L).Passed);
        Assert.True(Evaluator.Check(Eval(equals: "5"), 5m).Passed);
        Assert.True(Evaluator.Check(Eval(equals: "5.0"), 5).Passed);
        Assert.True(Evaluator.Check(Eval(equals: "0.5"), 0.5d).Passed);
    }

    [Fact]
    public void Equals_with_tolerance()
    {
        Assert.True(Evaluator.Check(Eval(equals: "0.9", tolerance: 0.05), 0.93).Passed);
        Assert.False(Evaluator.Check(Eval(equals: "0.9", tolerance: 0.05), 0.96).Passed);
    }

    [Fact]
    public void Equals_falls_back_to_string_comparison()
    {
        Assert.True(Evaluator.Check(Eval(equals: "Active"), "active").Passed);
        Assert.False(Evaluator.Check(Eval(equals: "Active"), "Inactive").Passed);
    }

    [Fact]
    public void Equals_null_handling()
    {
        Assert.True(Evaluator.Check(Eval(equals: "null"), null).Passed);
        Assert.False(Evaluator.Check(Eval(equals: "5"), null).Passed);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(5, true)]
    [InlineData(4, false)]
    [InlineData(21, false)]
    public void Min_max_bounds_inclusive(int actual, bool pass)
    {
        Assert.Equal(pass, Evaluator.Check(Eval(min: "5", max: "20"), actual).Passed);
    }

    [Fact]
    public void Between_bounds_inclusive()
    {
        var between = new List<string> { "0.85", "0.95" };
        Assert.True(Evaluator.Check(Eval(between: between), 0.85).Passed);
        Assert.True(Evaluator.Check(Eval(between: between), 0.95).Passed);
        Assert.False(Evaluator.Check(Eval(between: between), 0.84).Passed);
        Assert.False(Evaluator.Check(Eval(between: between), 0.96).Passed);
    }

    [Fact]
    public void Numeric_expectation_against_non_numeric_value_fails_with_error()
    {
        var result = Evaluator.Check(Eval(min: "5"), "not-a-number");
        Assert.False(result.Passed);
        Assert.NotNull(result.Error);

        var nullResult = Evaluator.Check(Eval(min: "5"), null);
        Assert.False(nullResult.Passed);
        Assert.Contains("NULL", nullResult.Error);
    }

    [Fact]
    public void Missing_expectation_is_informational_pass()
    {
        var result = Evaluator.Check(Eval(noExpect: true), 123);
        Assert.True(result.Passed);
        Assert.True(result.Informational);
        Assert.Equal(123, result.Value);
    }
}
