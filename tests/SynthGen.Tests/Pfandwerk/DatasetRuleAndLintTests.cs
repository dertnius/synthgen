using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>Dataset fix keys in gap rules (D-A1/A2) and the offline lint of D-B3.</summary>
public sealed class DatasetRuleAndLintTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfw-lint-" + Guid.NewGuid().ToString("N"));

    public DatasetRuleAndLintTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "datasets"));
        File.WriteAllText(Path.Combine(_dir, "datasets", "currencies.yaml"), """
            columns: [CurrencyCode, Name]
            rows:
              - [EUR, Euro]
              - [USD, US Dollar]
            """);
        File.WriteAllText(Path.Combine(_dir, "datasets", "colors.yaml"), """
            columns: [Color]
            rows: [[Red], [Blue]]
            """);
    }

    private DatasetStore Store() => DatasetStore.LoadDirectory(Path.Combine(_dir, "datasets"));

    private static string Rule(string kind, string fix, string column = "Name", string extra = "") => $"""
        rules:
          - id: X-001
            table: dbo.Currency
            key: CurrencyCode
            column: {column}
            kind: {kind}
            gap: "{column} IS NULL"
            fix: {fix}
            threshold: 10
            reason: because
        {extra}
        """;

    [Fact]
    public void An_ephemeral_dataset_fix_resolves_against_the_rules_files_datasets() =>
        GapRulesLoader.Load(Rule("ephemeral", "dataset.colors", column: "Color"), Store());

    [Fact]
    public void A_derived_dataset_fix_needs_inputs_that_key_the_rows()
    {
        GapRulesLoader.Load(Rule("derived", "dataset.currencies", extra: "    inputs: [CurrencyCode]"), Store());

        var ex = Assert.Throws<RulesLoadException>(() => GapRulesLoader.Load(
            Rule("derived", "dataset.currencies", extra: "    inputs: [Symbol]"), Store()));
        Assert.Contains("not columns of dataset", ex.Message);
    }

    [Fact]
    public void Identity_rules_may_not_use_a_dataset()
    {
        var ex = Assert.Throws<RulesLoadException>(() => GapRulesLoader.Load(
            Rule("identity", "dataset.currencies"), Store()));
        Assert.Contains("cannot mint identities", ex.Message);
    }

    [Fact]
    public void Datasets_have_no_floor()
    {
        var ex = Assert.Throws<RulesLoadException>(() => GapRulesLoader.Load(
            Rule("derived", "dataset.currencies",
                 extra: "    inputs: [CurrencyCode]\n    onMissingInput: floor"), Store()));
        Assert.Contains("no floor", ex.Message);
    }

    [Fact]
    public void An_unknown_dataset_fails_at_load_with_the_available_names()
    {
        var ex = Assert.Throws<RulesLoadException>(() => GapRulesLoader.Load(
            Rule("ephemeral", "dataset.nope"), Store()));
        Assert.Contains("colors", ex.Message);
        Assert.Contains("currencies", ex.Message);
    }

    [Fact]
    public void Lint_reports_every_finding_not_the_first()
    {
        var rulesPath = Path.Combine(_dir, "gaps.yaml");
        File.WriteAllText(rulesPath, """
            rules:
              - id: A-001
                table: dbo.T
                key: Id
                column: C
                kind: ephemeral
                gap: "C IS NULL"
                fix: not.a.generator
                threshold: 10
                reason: r
              - id: A-002
                table: dbo.T
                key: Id
                column: D
                kind: derived
                gap: "D IS NULL"
                fix: security.bathroomsFromRooms
                inputs: [Rooms]
                threshold: 0
                reason: r
            """);

        var lint = Linter.Run(rulesPath, surveyPath: null, testsDir: null);
        Assert.Equal("fail", lint.Verdict);
        Assert.Contains(lint.Findings, f => f.Rule == "A-001" && f.Message.Contains("unknown fix key"));
        Assert.Contains(lint.Findings, f => f.Rule == "A-002" && f.Message.Contains("threshold"));
        // A-002 is derived code with no invariant and no reachable tests directory.
        Assert.Contains(lint.Findings, f => f.Rule == "A-002" && f.Check == "invariant");
        Assert.Contains(lint.Findings, f => f.Rule == "A-002" && f.Check == "tests");
    }

    [Fact]
    public void Lint_flags_a_threshold_the_surveyed_null_count_would_block()
    {
        var rulesPath = Path.Combine(_dir, "gaps.yaml");
        File.WriteAllText(rulesPath, Rule("ephemeral", "dataset.colors", column: "Color"));

        var surveyPath = Path.Combine(_dir, "survey.json");
        Json.Write(surveyPath, new SurveyDocument("now", new List<TableSurvey>
        {
            new("dbo.Currency", 500, new List<ColumnSurvey>
            {
                new("Color", "nvarchar", true, false, 400, 0.8, 3,
                    new List<ValueCount>(), null, new List<string>()),
            }),
        }));

        var lint = Linter.Run(rulesPath, surveyPath, testsDir: null);
        Assert.Contains(lint.Findings, f => f.Check == "threshold" && f.Message.Contains("400"));
    }

    [Fact]
    public void The_shipped_rules_pass_lint_against_the_shipped_tests()
    {
        var root = Path.GetDirectoryName(GapRulesLoaderTests.FindRepoFile("rules/gaps.yaml"))!;
        var lint = Linter.Run(Path.Combine(root, "gaps.yaml"), null,
                              Path.Combine(Path.GetDirectoryName(root)!, "tests"));
        Assert.True(lint.Verdict == "pass",
            string.Join("\n", lint.Findings.Select(f => f.Message)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
