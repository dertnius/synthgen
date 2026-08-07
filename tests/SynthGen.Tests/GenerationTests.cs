using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;
using SynthGen.Core.Rules;

namespace SynthGen.Tests;

public class GenerationTests
{
    private const string Ddl = """
        CREATE TABLE [dbo].[T]
        (
            [Id]        INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [Code]      VARCHAR(10) NOT NULL,
            [Age]       INT NOT NULL,
            [Amount]    DECIMAL(10,2) NOT NULL,
            [Born]      DATE NULL,
            [Flag]      BIT NOT NULL,
            [Ref]       UNIQUEIDENTIFIER NOT NULL,
            [Status]    VARCHAR(8) NOT NULL,
            [Seq]       BIGINT NOT NULL,
            [Name]      NVARCHAR(5) NOT NULL
        );
        """;

    private static RulesFile Rules(int rows = 100, int? seed = 42) => RulesLoader.Load($$"""
        rows: {{rows}}
        {{(seed is null ? "" : $"seed: {seed}")}}
        columns:
          Code:
            strategy: template
            template: C-{row}
            unique: true
          Age:
            strategy: int
            min: 18
            max: 65
          Amount:
            strategy: decimal
            min: 10
            max: 99.5
          Born:
            strategy: date
            min: 1990-01-01
            max: 2000-12-31
            nullRate: 0.3
          Flag:
            strategy: bool
            trueRate: 0.8
          Ref:
            strategy: guid
            unique: true
          Status:
            strategy: pick
            values: [A, B, C]
            weights: [0.6, 0.3, 0.1]
          Seq:
            strategy: sequence
            start: 1000
            step: 5
          Name:
            strategy: faker
            method: name.firstName
        """);

    private static List<object?[]> Generate(RulesFile rules)
    {
        var table = DdlParser.ParseSingle(Ddl);
        var plan = GenerationPlan.Build(table, rules);
        return new RowGenerator(plan).Rows().ToList();
    }

    [Fact]
    public void Same_seed_produces_identical_output()
    {
        var a = Generate(Rules());
        var b = Generate(Rules());

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(
                a[i].Select(v => v is byte[] bytes ? Convert.ToHexString(bytes) : v),
                b[i].Select(v => v is byte[] bytes ? Convert.ToHexString(bytes) : v));
    }

    [Fact]
    public void Different_seed_produces_different_output()
    {
        var a = Generate(Rules(seed: 1));
        var b = Generate(Rules(seed: 2));
        Assert.NotEqual(
            a.SelectMany(r => r).Select(v => v?.ToString()),
            b.SelectMany(r => r).Select(v => v?.ToString()));
    }

    [Fact]
    public void Honors_ranges_types_and_null_rates()
    {
        var rows = Generate(Rules(rows: 2000));
        var table = DdlParser.ParseSingle(Ddl);
        var plan = GenerationPlan.Build(table, Rules(rows: 2000));
        int Col(string name) => plan.Columns.FindIndex(c =>
            string.Equals(c.Column.Name, name, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2000, rows.Count);

        foreach (var row in rows)
        {
            var age = Assert.IsType<int>(row[Col("Age")]);
            Assert.InRange(age, 18, 65);

            var amount = Assert.IsType<decimal>(row[Col("Amount")]);
            Assert.InRange(amount, 10m, 99.5m);
            Assert.Equal(Math.Round(amount, 2), amount); // decimal(10,2) scale applied

            if (row[Col("Born")] is not null)
            {
                var born = Assert.IsType<DateTime>(row[Col("Born")]);
                Assert.InRange(born, new DateTime(1990, 1, 1), new DateTime(2000, 12, 31));
                Assert.Equal(born.Date, born); // date strategy has no time part
            }

            Assert.IsType<long>(row[Col("Seq")]);
            Assert.IsType<Guid>(row[Col("Ref")]);
            Assert.Contains((string)row[Col("Status")]!, new[] { "A", "B", "C" });
        }

        double nullShare = rows.Count(r => r[Col("Born")] is null) / (double)rows.Count;
        Assert.InRange(nullShare, 0.25, 0.35);

        double trueShare = rows.Count(r => (bool)r[Col("Flag")]!) / (double)rows.Count;
        Assert.InRange(trueShare, 0.75, 0.85);

        // Weighted pick: A should clearly dominate C.
        int a = rows.Count(r => (string)r[Col("Status")]! == "A");
        int c = rows.Count(r => (string)r[Col("Status")]! == "C");
        Assert.True(a > c * 3, $"expected A ({a}) >> C ({c})");
    }

    [Fact]
    public void Unique_and_sequence_columns_have_no_duplicates()
    {
        var rows = Generate(Rules(rows: 500));
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(Ddl), Rules(rows: 500));
        int Col(string name) => plan.Columns.FindIndex(c => c.Column.Name == name);

        Assert.Equal(500, rows.Select(r => r[Col("Code")]).Distinct().Count());
        Assert.Equal(500, rows.Select(r => r[Col("Ref")]).Distinct().Count());

        var seqs = rows.Select(r => (long)r[Col("Seq")]!).ToList();
        Assert.Equal(1000, seqs[0]);
        Assert.Equal(1000 + 5 * 499, seqs[^1]);
    }

    [Fact]
    public void Strings_truncate_to_ddl_length_and_are_counted()
    {
        // Name is nvarchar(5); faker first names are often longer.
        var table = DdlParser.ParseSingle(Ddl);
        var rules = Rules(rows: 200);
        var plan = GenerationPlan.Build(table, rules);
        var generator = new RowGenerator(plan);
        var rows = generator.Rows().ToList();
        int col = plan.Columns.FindIndex(c => c.Column.Name == "Name");

        Assert.All(rows, r => Assert.True(((string)r[col]!).Length <= 5));
        Assert.True(generator.Truncations.ContainsKey("Name"));
    }

    [Fact]
    public void Identity_column_is_skipped_unless_explicit()
    {
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(Ddl), Rules());
        Assert.DoesNotContain(plan.Columns, c => c.Column.Name == "Id");
        Assert.False(plan.KeepIdentity);

        var withIdentity = Rules();
        withIdentity.Columns["Id"] = new ColumnRule { Strategy = "sequence", Start = 1, Step = 1 };
        var plan2 = GenerationPlan.Build(DdlParser.ParseSingle(Ddl), withIdentity);
        Assert.Contains(plan2.Columns, c => c.Column.Name == "Id");
        Assert.True(plan2.KeepIdentity);
    }

    [Fact]
    public void Unknown_rule_column_fails_fast()
    {
        var rules = Rules();
        rules.Columns["Nope"] = new ColumnRule { Strategy = "int" };
        Assert.Throws<GenerationException>(() =>
            GenerationPlan.Build(DdlParser.ParseSingle(Ddl), rules));
    }

    [Fact]
    public void Unique_exhaustion_gives_actionable_error()
    {
        var ddl = "CREATE TABLE T (V INT NOT NULL)";
        var rules = RulesLoader.Load("""
            rows: 50
            seed: 1
            columns:
              V:
                strategy: int
                min: 1
                max: 3
                unique: true
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(ddl), rules);
        var ex = Assert.Throws<GenerationException>(() => new RowGenerator(plan).Rows().ToList());
        Assert.Contains("unique", ex.Message);
        Assert.Contains("Widen", ex.Message);
    }

    [Fact]
    public void Unique_is_enforced_on_the_truncated_value()
    {
        // "ID-{row}" truncates to "ID" on a char(2) column; unique must reject that
        // instead of silently loading duplicate keys.
        var ddl = "CREATE TABLE T (Code CHAR(2) NOT NULL PRIMARY KEY)";
        var rules = RulesLoader.Load("""
            rows: 5
            seed: 1
            columns:
              Code:
                strategy: template
                template: ID-{row}
                unique: true
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(ddl), rules);
        Assert.Throws<GenerationException>(() => new RowGenerator(plan).Rows().ToList());
    }

    [Fact]
    public void Short_string_pk_scaffolds_unique_random_string()
    {
        var ddl = "CREATE TABLE T (Code CHAR(2) NOT NULL PRIMARY KEY, Name NVARCHAR(50) NULL)";
        var table = DdlParser.ParseSingle(ddl);
        var rules = RulesLoader.Load(RuleScaffolder.Scaffold(table, rows: 50));

        Assert.Equal("string", rules.Columns["Code"].Strategy);
        Assert.Equal(2, rules.Columns["Code"].Length);
        Assert.True(rules.Columns["Code"].Unique);

        // And the scaffold generates cleanly: 50 distinct 2-char codes.
        var plan = GenerationPlan.Build(table, rules);
        var rows = new RowGenerator(plan).Rows().ToList();
        int col = plan.Columns.FindIndex(c => c.Column.Name == "Code");
        Assert.Equal(50, rows.Select(r => (string)r[col]!).Distinct().Count());
    }

    [Fact]
    public void Rules_without_strategy_fall_back_to_inference()
    {
        var rules = RulesLoader.Load("""
            rows: 20
            seed: 5
            columns:
              Born:
                nullRate: 1.0
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(Ddl), rules);
        var generator = new RowGenerator(plan);
        int col = plan.Columns.FindIndex(c => c.Column.Name == "Born");

        // Inferred strategy for a DATE column, with the explicit nullRate layered on.
        Assert.All(generator.Rows(), r => Assert.Null(r[col]));
    }

    [Fact]
    public void Template_tokens_expand()
    {
        var ddl = "CREATE TABLE T (V VARCHAR(50) NOT NULL)";
        var rules = RulesLoader.Load("""
            rows: 3
            seed: 9
            columns:
              V:
                strategy: template
                template: 'row{row}-{rand:100-999}'
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(ddl), rules);
        var rows = new RowGenerator(plan).Rows().ToList();

        for (int i = 0; i < rows.Count; i++)
        {
            var value = (string)rows[i][0]!;
            Assert.StartsWith($"row{i + 1}-", value);
            var suffix = int.Parse(value.Split('-')[1]);
            Assert.InRange(suffix, 100, 999);
        }
    }

    [Fact]
    public void Query_strategy_uses_lookup_data()
    {
        var ddl = "CREATE TABLE T (Code CHAR(2) NOT NULL)";
        var rules = RulesLoader.Load("""
            rows: 100
            seed: 3
            columns:
              Code:
                strategy: query
                query: SELECT Code FROM X
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(ddl), rules);
        Assert.Equal("SELECT Code FROM X", plan.Lookups["Code"]);

        var lookups = new Dictionary<string, IReadOnlyList<object>>
        {
            ["Code"] = new object[] { "US", "GB", "DE" },
        };
        var rows = new RowGenerator(plan, lookups).Rows().ToList();
        Assert.All(rows, r => Assert.Contains((string)r[0]!, new[] { "US", "GB", "DE" }));

        // Missing lookup data fails with a clear message instead of NullReference.
        var ex = Assert.Throws<GenerationException>(() => new RowGenerator(plan));
        Assert.Contains("query strategy", ex.Message);
    }

    [Fact]
    public void NotNull_column_skipped_without_default_warns()
    {
        var ddl = "CREATE TABLE T (A INT NOT NULL, B INT NOT NULL)";
        var rules = RulesLoader.Load("""
            rows: 5
            columns:
              A:
                strategy: skip
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(ddl), rules);
        Assert.Contains(plan.Warnings, w => w.Contains("'A'") && w.Contains("NOT NULL"));
    }
}
