using SynthGen.Core.Ddl;
using SynthGen.Core.Rules;

namespace SynthGen.Tests;

public class RulesTests
{
    [Fact]
    public void Loads_full_rules_file()
    {
        var rules = RulesLoader.Load("""
            table: dbo.Customers
            rows: 500
            seed: 42
            truncateBeforeLoad: true
            batchSize: 2000
            columns:
              Email:
                strategy: template
                template: u{row}@x.test
                unique: true
              Age:
                strategy: int
                min: 18
                max: 90
                nullRate: 0.25
              Status:
                strategy: pick
                values: [A, B]
                weights: [0.8, 0.2]
            evaluations:
              - name: rowcount
                query: SELECT COUNT(*) FROM dbo.Customers
                expect:
                  equals: 500
              - name: ratio
                query: SELECT 0.5
                expect:
                  between: ['0.4', '0.6']
              - name: info-only
                query: SELECT AVG(Age) FROM dbo.Customers
            """);

        Assert.Equal("dbo.Customers", rules.Table);
        Assert.Equal(500, rules.Rows);
        Assert.Equal(42, rules.Seed);
        Assert.True(rules.TruncateBeforeLoad);
        Assert.Equal(2000, rules.BatchSize);

        Assert.Equal("template", rules.Columns["Email"].Strategy);
        Assert.True(rules.Columns["Email"].Unique);
        Assert.Equal(0.25, rules.Columns["Age"].NullRate);
        Assert.Equal(new[] { "A", "B" }, rules.Columns["Status"].Values!);

        Assert.Equal(3, rules.Evaluations.Count);
        Assert.Equal("500", rules.Evaluations[0].Expect!.EqualsValue);
        Assert.Equal(new[] { "0.4", "0.6" }, rules.Evaluations[1].Expect!.Between!);
        Assert.Null(rules.Evaluations[2].Expect);
    }

    [Theory]
    [InlineData("rows: 0")]
    [InlineData("rows: 10\ncolumns:\n  X:\n    strategy: pick")]
    [InlineData("rows: 10\ncolumns:\n  X:\n    strategy: faker")]
    [InlineData("rows: 10\ncolumns:\n  X:\n    nullRate: 1.5")]
    [InlineData("rows: 10\nevaluations:\n  - name: a\n    query: SELECT 1\n  - name: a\n    query: SELECT 2")]
    [InlineData("rows: 10\nevaluations:\n  - query: SELECT 1")]
    [InlineData("rows: 10\nevaluations:\n  - name: a\n    query: SELECT 1\n    expect:\n      equals: 1\n      min: 2")]
    public void Rejects_invalid_rules(string yaml)
    {
        Assert.Throws<RulesLoadException>(() => RulesLoader.Load(yaml));
    }

    [Fact]
    public void Scaffold_round_trips_through_loader()
    {
        var ddl = File.ReadAllText(Path.Combine(FindRepoRoot(), "samples", "customers.sql"));
        var table = DdlParser.ParseSingle(ddl, "dbo.Customers");

        var yaml = RuleScaffolder.Scaffold(table, rows: 250, seed: 7);
        var rules = RulesLoader.Load(yaml);

        Assert.Equal("dbo.Customers", rules.Table);
        Assert.Equal(250, rules.Rows);
        Assert.Equal(7, rules.Seed);

        // Identity column scaffolds as skip; FK column scaffolds a lookup query.
        Assert.Equal("skip", rules.Columns["CustomerId"].Strategy);
        Assert.Equal("query", rules.Columns["CountryCode"].Strategy);
        Assert.Contains("Countries", rules.Columns["CountryCode"].Query);

        // Name-based inference kicks in for text columns.
        Assert.Equal("faker", rules.Columns["FirstName"].Strategy);
        Assert.Equal("name.firstName", rules.Columns["FirstName"].Method);
        Assert.Equal("internet.email", rules.Columns["Email"].Method);

        // Evaluations include row count plus not-null and uniqueness checks.
        Assert.Contains(rules.Evaluations, e => e.Name == "row-count" && e.Expect!.EqualsValue == "250");
        Assert.Contains(rules.Evaluations, e => e.Name.StartsWith("not-null-"));
        Assert.Contains(rules.Evaluations, e => e.Name == "unique-email");
    }

    [Fact]
    public void Scaffold_skips_computed_and_rowversion()
    {
        var ddl = File.ReadAllText(Path.Combine(FindRepoRoot(), "samples", "customers.sql"));
        var table = DdlParser.ParseSingle(ddl, "dbo.Customers");
        var rules = RulesLoader.Load(RuleScaffolder.Scaffold(table));

        Assert.Equal("skip", rules.Columns["FullName"].Strategy);
        Assert.Equal("skip", rules.Columns["RowVer"].Strategy);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SynthGen.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
