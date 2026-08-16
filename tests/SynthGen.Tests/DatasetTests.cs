using Bogus;
using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;
using SynthGen.Core.Rules;

namespace SynthGen.Tests;

/// <summary>Validation and lookup behavior of the reviewed-dataset files (D-A1).</summary>
public class DatasetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "synthgen-ds-" + Guid.NewGuid().ToString("N"));

    private const string Currencies = """
        columns: [CurrencyCode, Name]
        match:
          CurrencyCode: ["*currencycode*"]
        rows:
          - [EUR, Euro]
          - [USD, US Dollar]
          - [GBP, British Pound]
        """;

    private DatasetStore Store(params (string Name, string Yaml)[] files)
    {
        var datasets = Path.Combine(_dir, "datasets");
        Directory.CreateDirectory(datasets);
        foreach (var (name, yaml) in files)
            File.WriteAllText(Path.Combine(datasets, name + ".yaml"), yaml);
        return DatasetStore.LoadDirectory(datasets);
    }

    [Fact]
    public void A_missing_datasets_directory_is_an_empty_store_not_an_error()
    {
        var store = DatasetStore.ForRulesFile(Path.Combine(_dir, "nowhere", "gaps.yaml"));
        Assert.Empty(store.Names);
    }

    [Fact]
    public void Rows_must_match_the_declared_columns()
    {
        var ex = Assert.Throws<RulesLoadException>(() => Store(("bad", """
            columns: [Code, Name]
            rows:
              - [EUR, Euro]
              - [USD]
            """)));
        Assert.Contains("row 2", ex.Message);
    }

    [Fact]
    public void Weights_must_be_positive_and_one_per_row()
    {
        var countEx = Assert.Throws<RulesLoadException>(() => Store(("bad", """
            columns: [Code]
            rows: [[A], [B]]
            weights: [1]
            """)));
        Assert.Contains("1 weights for 2 rows", countEx.Message);

        var signEx = Assert.Throws<RulesLoadException>(() => Store(("bad", """
            columns: [Code]
            rows: [[A], [B]]
            weights: [1, 0]
            """)));
        Assert.Contains("positive", signEx.Message);
    }

    [Fact]
    public void Match_may_only_name_declared_columns()
    {
        var ex = Assert.Throws<RulesLoadException>(() => Store(("bad", """
            columns: [Code]
            match:
              Nope: ["*code*"]
            rows: [[A]]
            """)));
        Assert.Contains("'Nope'", ex.Message);
    }

    [Fact]
    public void Fix_keys_resolve_with_an_implicit_or_explicit_column()
    {
        var store = Store(("currencies", Currencies));

        var (_, byTarget) = store.ResolveFixKey("dataset.currencies", "Name");
        Assert.Equal(1, byTarget);

        var (_, explicitColumn) = store.ResolveFixKey("dataset.currencies.CurrencyCode", "Name");
        Assert.Equal(0, explicitColumn);
    }

    [Fact]
    public void An_unknown_dataset_or_column_names_what_is_available()
    {
        var store = Store(("currencies", Currencies));

        var unknown = Assert.Throws<RulesLoadException>(() => store.ResolveFixKey("dataset.colors", "Name"));
        Assert.Contains("currencies", unknown.Message);

        var badColumn = Assert.Throws<RulesLoadException>(
            () => store.ResolveFixKey("dataset.currencies.Symbol", "Name"));
        Assert.Contains("CurrencyCode, Name", badColumn.Message);
    }

    [Fact]
    public void Lookup_finds_the_correlated_row_case_insensitively()
    {
        var store = Store(("currencies", Currencies));
        Assert.True(store.TryGet("currencies", out var ds));

        Assert.Equal(0, ds.FindRow(new Dictionary<string, string> { ["currencycode"] = "eur" }));
        Assert.Equal(-1, ds.FindRow(new Dictionary<string, string> { ["CurrencyCode"] = "ZZZ" }));
        Assert.True(ds.ColumnsKeyRowsUniquely(new[] { "CurrencyCode" }));
    }

    [Fact]
    public void Weighted_picks_stay_in_range_and_follow_the_weights()
    {
        var store = Store(("skew", """
            columns: [V]
            rows: [[rare], [common]]
            weights: [1, 99]
            """));
        Assert.True(store.TryGet("skew", out var ds));

        var faker = new Faker { Random = new Randomizer(7) };
        var picks = Enumerable.Range(0, 200).Select(_ => ds.PickRow(faker)).ToList();
        Assert.All(picks, p => Assert.InRange(p, 0, 1));
        Assert.True(picks.Count(p => p == 1) > 150, "the 99-weight row should dominate");
    }

    [Fact]
    public void The_name_match_hook_infers_the_dataset_strategy()
    {
        var store = Store(("currencies", Currencies));
        var table = DdlParser.ParseSingle("""
            CREATE TABLE [dbo].[T](
                [Id] INT NOT NULL PRIMARY KEY,
                [Currency_Code] NVARCHAR(3) NOT NULL
            );
            """, null);

        var rule = ColumnInference.Infer(table.FindColumn("Currency_Code")!, table, store);
        Assert.Equal("dataset", rule.Strategy);
        Assert.Equal("currencies", rule.Dataset);
        Assert.Equal("CurrencyCode", rule.DatasetColumn);

        // Without a store the same column falls back to the faker heuristics.
        Assert.NotEqual("dataset", ColumnInference.Infer(table.FindColumn("Currency_Code")!, table).Strategy);
    }

    [Fact]
    public void Correlated_columns_draw_from_one_dataset_row_even_under_unique_retries()
    {
        var store = Store(("currencies", Currencies));
        var table = DdlParser.ParseSingle("""
            CREATE TABLE [dbo].[Currency](
                [Code] NCHAR(3) NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(50) NULL
            );
            """, null);
        var rules = RulesLoader.Load("""
            rows: 3
            seed: 11
            columns:
              Code:
                strategy: dataset
                dataset: currencies
                datasetColumn: CurrencyCode
                unique: true
              Name:
                strategy: dataset
                dataset: currencies
            """);

        var pairs = new Dictionary<string, string>
            { ["EUR"] = "Euro", ["USD"] = "US Dollar", ["GBP"] = "British Pound" };
        var generator = new RowGenerator(GenerationPlan.Build(table, rules, store), datasets: store);

        var rows = generator.Rows().ToList();
        // unique: true forces all three distinct codes, and each keeps its own name.
        Assert.Equal(pairs.Keys.OrderBy(k => k), rows.Select(r => (string)r[0]!).OrderBy(k => k));
        foreach (var row in rows)
            Assert.Equal(pairs[(string)row[0]!], (string)row[1]!);
    }

    [Fact]
    public void The_dataset_strategy_requires_a_dataset_name() =>
        Assert.Contains("requires 'dataset'", Assert.Throws<RulesLoadException>(() => RulesLoader.Load("""
            columns:
              X:
                strategy: dataset
            """)).Message);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
