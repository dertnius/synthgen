using Bogus;
using Pfandwerk;

namespace Pfandwerk.Tests;

public class GeneratorTests
{
    private static object Bathrooms(int? rooms) =>
        PatchGenerators.Derived("security.bathroomsFromRooms",
            new Dictionary<string, object?> { ["Rooms"] = rooms });

    [Theory]
    // One bathroom minimum, one more per three rooms. 3 -> 1 and 4 -> 2 are the two
    // boundaries the specification calls out by name.
    [InlineData(1, 1)] [InlineData(2, 1)] [InlineData(3, 1)]
    [InlineData(4, 2)] [InlineData(6, 2)] [InlineData(7, 3)]
    [InlineData(9, 3)] [InlineData(10, 4)] [InlineData(0, 1)]
    public void Bathrooms_follow_the_room_count(int rooms, int expected) =>
        Assert.Equal(expected, Bathrooms(rooms));

    [Fact]
    public void Bathrooms_refuse_a_null_room_count()
    {
        // The generator must not invent a value; the planner turns this into a skipped row
        // so a human decides, rather than silently writing the floor.
        var ex = Assert.Throws<GeneratorException>(() => Bathrooms(null));
        Assert.Contains("NULL", ex.Message);
    }

    [Fact]
    public void Unknown_fix_key_is_rejected_with_the_known_list()
    {
        var ex = Assert.Throws<GeneratorException>(
            () => PatchGenerators.Random("security.notAThing", new Faker()));
        Assert.Contains("security.bathroomsFromRooms", ex.Message);
    }

    [Fact]
    public void Faker_map_from_synthgen_is_still_reachable() =>
        Assert.NotNull(PatchGenerators.Random("name.firstName", new Faker()));

    [Fact]
    public void Canonical_formatting_is_invariant_culture() =>
        Assert.Equal("1234.5", Canonical.Format(1234.5m));
}

public class GapPredicateValidatorTests
{
    private static GapRule Rule(string gap) => new()
    {
        Id = "T-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
        Kind = "ephemeral", Gap = gap, Fix = "property.energyClass", Threshold = 10, Reason = "t",
    };

    [Fact]
    public void Accepts_a_multi_clause_predicate_on_the_declared_table() =>
        GapPredicateValidator.Validate(Rule("PropertyType = 'EFH' AND Bathrooms IS NULL"));

    [Fact]
    public void Rejects_a_statement_terminator()
    {
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapPredicateValidator.Validate(Rule("1=1; DROP TABLE dbo.Security")));
        Assert.Contains("statement terminator", ex.Message);
    }

    [Fact]
    public void Rejects_a_subquery_against_an_undeclared_table()
    {
        // Hard rule 4: without the AST walk this predicate reads a table the rule never
        // declares, and nothing else in the pipeline would notice.
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapPredicateValidator.Validate(
                Rule("PropertyId IN (SELECT PropertyId FROM dbo.Secrets)")));
        Assert.Contains("dbo.Secrets", ex.Message);
    }
}

public class GapRulesLoaderTests
{
    private const string Base = """
        rules:
          - id: X-001
            table: dbo.Security
            key: PropertyId
            column: Bathrooms
            kind: {0}
            gap: "Bathrooms IS NULL"
            fix: security.bathroomsFromRooms
            threshold: 10
            reason: because
        {1}
        """;

    private static string Yaml(string kind, string extra = "") =>
        Base.Replace("{0}", kind).Replace("{1}", extra);

    [Fact]
    public void Derived_rules_require_inputs()
    {
        var ex = Assert.Throws<GapRulesLoadException>(() => GapRulesLoader.Load(Yaml("derived")));
        Assert.Contains("require 'inputs'", ex.Message);
    }

    [Fact]
    public void Non_derived_rules_may_not_carry_derived_only_fields()
    {
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapRulesLoader.Load(Yaml("ephemeral", "    inputs: [Rooms]")));
        Assert.Contains("only to derived rules", ex.Message);
    }

    [Fact]
    public void Derived_rules_default_to_blocking_on_a_missing_input()
    {
        var file = GapRulesLoader.Load(Yaml("derived", "    inputs: [Rooms]"));
        Assert.Equal(MissingInputPolicy.Block, file.Rules[0].ParsedMissingInputPolicy);
    }

    [Fact]
    public void The_shipped_rules_file_loads()
    {
        var file = GapRulesLoader.LoadFile(FindRepoFile("rules/gaps.yaml"));
        Assert.Contains(file.Rules, r => r.Id == "SEC-001" && r.ParsedKind == RuleKind.Derived);
        Assert.Contains(file.Rules, r => r.Id == "SEC-002" && r.ParsedKind == RuleKind.Identity);
    }

    internal static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        return dir is null
            ? throw new FileNotFoundException($"could not locate {relative} above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, relative);
    }
}

public class GapQueryTests
{
    private static readonly GapRule Derived = new()
    {
        Id = "SEC-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
        Kind = "derived", Gap = "PropertyType = 'EFH' AND Bathrooms IS NULL",
        Fix = "security.bathroomsFromRooms", Inputs = new List<string> { "Rooms" },
        Threshold = 500, Reason = "t",
    };

    [Fact]
    public void Rows_selects_the_key_the_column_and_the_declared_inputs()
    {
        var sql = GapQuery.Rows(Derived);
        Assert.Contains("PropertyId", sql);
        Assert.Contains("Bathrooms", sql);
        Assert.Contains("Rooms", sql);
    }

    [Fact]
    public void Count_and_Rows_share_one_predicate()
    {
        // The same predicate text must reach every call site; if these ever diverge, SCAN
        // and VERIFY can disagree about what a gap is.
        Assert.Contains(Derived.Gap, GapQuery.Count(Derived));
        Assert.Contains(Derived.Gap, GapQuery.Rows(Derived));
        Assert.Contains(Derived.Gap, GapQuery.RemainingAmong(Derived, new[] { "101" }));
    }

    [Fact]
    public void Numeric_keys_are_not_quoted_but_string_keys_are()
    {
        Assert.Contains("IN (101)", GapQuery.RemainingAmong(Derived, new[] { "101" }));
        Assert.Contains("IN ('A-1')", GapQuery.RemainingAmong(Derived, new[] { "A-1" }));
    }

    private static readonly GapRule WithInvariant = new()
    {
        Id = "SEC-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
        Kind = "derived", Gap = "PropertyType = 'EFH' AND Bathrooms IS NULL",
        Fix = "security.bathroomsFromRooms", Inputs = new List<string> { "Rooms" },
        Invariant = "Bathrooms IS NOT NULL", Threshold = 500, Reason = "t",
    };

    [Fact]
    public void The_coverage_queries_carry_both_of_the_rules_predicates()
    {
        Assert.Contains(WithInvariant.Invariant!, GapQuery.InvariantHolds(WithInvariant));

        var uncovered = GapQuery.UncoveredViolations(WithInvariant);
        Assert.Contains(WithInvariant.Invariant!, uncovered);
        Assert.Contains(WithInvariant.Gap, uncovered);

        var overBroad = GapQuery.SelectedButValid(WithInvariant);
        Assert.Contains(WithInvariant.Invariant!, overBroad);
        Assert.Contains(WithInvariant.Gap, overBroad);
    }

    [Fact]
    public void Uncovered_excludes_the_gap_by_key_rather_than_by_negating_it()
    {
        // NOT (gap) is UNKNOWN wherever the predicate touches a NULL, which drops exactly
        // the rows this query exists to find. Pinned in SQL so a later simplification to the
        // obvious form fails here rather than in production silence.
        var sql = GapQuery.UncoveredViolations(WithInvariant);
        Assert.Contains($"{WithInvariant.Key} NOT IN (SELECT {WithInvariant.Key}", sql);
        Assert.DoesNotContain($"NOT ({WithInvariant.Gap})", sql);
    }
}

public class FixKeyValidationTests
{
    private static string Yaml(string kind, string fix, string extra = "") => $"""
        rules:
          - id: X-001
            table: dbo.Security
            key: PropertyId
            column: Bathrooms
            kind: {kind}
            gap: "Bathrooms IS NULL"
            fix: {fix}
            threshold: 10
            reason: because
        {extra}
        """;

    [Fact]
    public void An_unknown_fix_key_fails_at_load_not_when_a_row_first_matches()
    {
        // Lazily resolving the generator meant a typo stayed dormant until the day the data
        // went bad — the worst possible day to be debugging the rules file.
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapRulesLoader.Load(Yaml("ephemeral", "not.a.generator")));
        Assert.Contains("unknown fix key", ex.Message);
    }

    [Fact]
    public void A_derived_generator_on_a_random_rule_is_rejected()
    {
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapRulesLoader.Load(Yaml("ephemeral", "security.bathroomsFromRooms")));
        Assert.Contains("needs kind: derived", ex.Message);
    }

    [Fact]
    public void A_random_generator_on_a_derived_rule_is_rejected()
    {
        var ex = Assert.Throws<GapRulesLoadException>(
            () => GapRulesLoader.Load(Yaml("derived", "internet.email", "    inputs: [Rooms]")));
        Assert.Contains("not a derived generator", ex.Message);
    }

    [Fact]
    public void A_synthgen_faker_key_is_accepted_without_pfandwerk_redeclaring_it() =>
        GapRulesLoader.Load(Yaml("ephemeral", "internet.email"));
}
