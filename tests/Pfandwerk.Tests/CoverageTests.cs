using Dapper;
using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>
/// Eight checks validate a rule today and none of them asks whether its gap predicate
/// selects the rows a person meant. Coverage cross-checks the gap against the rule's own
/// invariant and reports three findings, all advisory.
///
/// <para>The half that matters most is the third. <c>GapQuery.InvariantViolations</c> is
/// <c>NOT (invariant)</c>, and under three-valued logic a row the invariant cannot evaluate
/// is neither returned as a violation nor counted as a pass — VERIFY layer 2 has been
/// reporting green on rows it never judged. These tests pin that discrepancy so it cannot
/// quietly come back.</para>
/// </summary>
public sealed class CoverageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-coverage").FullName;

    private DbContext Db(string name)
    {
        var path = Path.Combine(_dir, name + ".db");
        return new DbContext(Provider.Sqlite, path, path);
    }

    private DbContext Seed(string name, params (int Id, int? Size, string? Status)[] rows)
    {
        var db = Db(name);
        using var conn = db.OpenTarget();
        conn.Execute("""
            CREATE TABLE dbo.Widget (
                WidgetId INTEGER NOT NULL PRIMARY KEY, Size INTEGER NULL, Status TEXT NULL)
            """);
        foreach (var (id, size, status) in rows)
            conn.Execute("INSERT INTO dbo.Widget VALUES (@id, @size, @status)",
                         new { id, size, status });
        new LedgerRepository(db).EnsureCreated();
        return db;
    }

    private static GapRule Rule(string gap, string invariant) => new()
    {
        Id = "WID-001", Table = "dbo.Widget", Key = "WidgetId", Column = "Size",
        Kind = "ephemeral", Gap = gap, Fix = "property.yearBuilt", Threshold = 100,
        Reason = "widgets need a size", Invariant = invariant,
    };

    private static RuleCoverage? Coverage(DbContext db, GapRule rule) =>
        new Planner(db, new LedgerRepository(db), new List<GapRule> { rule }, seed: 1)
            .Plan("coverage-test", "rulessha").Rules.Single().Coverage;

    /// <summary>
    /// Four widgets. Row 2 is broken and archived, row 4 is broken with a NULL in the very
    /// column the gap predicate tests — the two shapes a too-narrow predicate misses.
    /// </summary>
    private DbContext Widgets(string name) => Seed(name,
        (1, null, "active"),        // gap TRUE,    invariant FALSE — correctly selected
        (2, null, "archived"),      // gap FALSE,   invariant FALSE — missed
        (3, 5, "active"),           // gap FALSE,   invariant TRUE  — rightly left alone
        (4, null, null));           // gap UNKNOWN, invariant FALSE — missed, and invisible
                                    //                                to the naive query

    private const string Guarded = "Size IS NOT NULL AND Size >= 1";

    // ------------------------------------------------------------ too narrow

    [SqliteFact]
    public void Rows_the_gap_does_not_reach_are_reported_as_uncovered()
    {
        var db = Widgets("narrow");
        var c = Coverage(db, Rule("Size IS NULL AND Status = 'active'", Guarded))!;

        Assert.Equal(2, c.UncoveredViolations);
        Assert.Equal(new[] { "2", "4" }, c.UncoveredKeys);
        Assert.Equal(0, c.SelectedButValid);
    }

    [SqliteFact]
    public void A_null_in_a_predicate_column_still_appears_as_uncovered()
    {
        // The case that made the obvious SQL wrong. Row 4 has a NULL in Status, so
        // NOT (Size IS NULL AND Status = 'active') is UNKNOWN and the row vanishes from a
        // NOT (gap) formulation — precisely the row most worth surfacing. The shipped query
        // uses key NOT IN (gap set) instead, which is NULL-safe because the subquery returns
        // only rows the gap is definitely TRUE for.
        var db = Widgets("nulltrap");
        var rule = Rule("Size IS NULL AND Status = 'active'", Guarded);

        Assert.Contains("4", Coverage(db, rule)!.UncoveredKeys);

        using var conn = db.OpenTarget();
        var naive = conn.Query<string>(
            $"SELECT WidgetId FROM dbo.Widget WHERE NOT ({rule.Invariant}) AND NOT ({rule.Gap})").ToList();
        Assert.DoesNotContain("4", naive);
    }

    // ------------------------------------------------------------- too broad

    [SqliteFact]
    public void Rows_the_gap_selects_but_the_invariant_already_accepts_are_reported()
    {
        var db = Widgets("broad");
        var c = Coverage(db, Rule("Status = 'active' OR Size IS NULL", Guarded))!;

        // Row 3 is a healthy widget this predicate would overwrite.
        Assert.Equal(1, c.SelectedButValid);
        Assert.Equal(new[] { "3" }, c.SelectedButValidKeys);
        Assert.Equal(0, c.UncoveredViolations);
    }

    // ---------------------------------------------------------- indeterminate

    /// <summary>One row each of TRUE, FALSE and UNKNOWN under an unguarded invariant.</summary>
    private DbContext ThreeValued(string name) => Seed(name,
        (10, 1, "active"), (11, 0, "active"), (12, null, "active"));

    [SqliteFact]
    public void Rows_the_invariant_cannot_judge_are_counted_and_named()
    {
        var db = ThreeValued("unknown");
        var rule = Rule("Size IS NULL", "Size >= 1");     // no IS NOT NULL guard
        var c = Coverage(db, rule)!;

        Assert.Equal(1, c.Indeterminate);
        Assert.Equal(new[] { "12" }, c.IndeterminateKeys);

        // The discrepancy this exists to surface: layer 2's own query never mentions row 12,
        // so a green invariant check says nothing at all about it.
        using var conn = db.OpenTarget();
        var violations = conn.Query<string>(GapQuery.InvariantViolations(rule)).ToList();
        Assert.Equal(new[] { "11" }, violations);
    }

    [SqliteFact]
    public void A_guarded_invariant_judges_every_row()
    {
        // The fix for an indeterminate count is to rewrite the invariant, not the data.
        var db = ThreeValued("guarded");
        Assert.Equal(0, Coverage(db, Rule("Size IS NULL", Guarded))!.Indeterminate);
    }

    [SqliteFact]
    public void Verify_says_how_many_rows_its_invariant_could_not_judge()
    {
        var db = ThreeValued("verify");
        var rules = new List<GapRule> { Rule("Size IS NULL", "Size >= 1") };

        var plan = new Planner(db, new LedgerRepository(db), rules, seed: 1).Plan("v", "sha");
        var baseline = new BaselineDocument("v", CheckRunner.Run(db, CheckRunner.Invariants(rules)),
                                            new List<CheckResult>());
        var verify = new Verifier(db, rules).Verify(plan, baseline, Array.Empty<Check>());

        var invariant = verify.Invariants.Single(i => i.Name == "WID-001.invariant");
        Assert.Contains("1 row(s) not evaluated", invariant.Detail);
    }

    // ---------------------------------------------------------- shipped rules

    private static List<GapRule> Shipped() =>
        GapRulesLoader.LoadFile(GapRulesLoaderTests.FindRepoFile("rules/gaps.yaml")).Rules
            .Where(r => r.Table == "dbo.Security").ToList();

    /// <summary>The EndToEndTests fixture, seeded here so coverage can be read off it.</summary>
    private DbContext Security()
    {
        var db = Db("security");
        using (var conn = db.OpenTarget())
        {
            conn.Execute("""
                CREATE TABLE dbo.Security (
                    PropertyId   INTEGER NOT NULL PRIMARY KEY,
                    SecurityId   TEXT    NULL,
                    PropertyType TEXT    NOT NULL,
                    Rooms        INTEGER NULL,
                    Bathrooms    INTEGER NULL)
                """);
            conn.Execute("""
                INSERT INTO dbo.Security VALUES
                    (101, 'DE0001234567', 'EFH',    3, NULL),
                    (102, NULL,           'EFH',    5, NULL),
                    (103, 'DE0007654321', 'EFH',    7, NULL),
                    (104, NULL,           'EFH', NULL, NULL),
                    (105, 'DE0009999999', 'EFH',    9, NULL),
                    (106, 'DE0001111111', 'MFH',   12,    4),
                    (107, 'DE0002222222', 'WHG',    2,    1),
                    (108, 'DE0003333333', 'EFH',    4,    2)
                """);
        }
        new LedgerRepository(db).EnsureCreated();
        return db;
    }

    [SqliteFact]
    public void The_shipped_security_rule_covers_its_invariant_exactly()
    {
        // Nothing to report is the goal state, and worth pinning: SEC-001's gap selects every
        // row its invariant rejects and no row it accepts. Row 104 is not uncovered — it is
        // selected, and skipped later for a NULL room count, which is a different thing.
        var c = Coverage(Security(), Shipped().Single(r => r.Id == "SEC-001"))!;

        Assert.Equal(0, c.UncoveredViolations);
        Assert.Equal(0, c.SelectedButValid);
        Assert.Equal(0, c.Indeterminate);
    }

    [SqliteFact]
    public void A_rule_without_an_invariant_has_nothing_to_cross_check()
    {
        // SEC-002 mints identities; there is no predicate to compare its gap against.
        Assert.Null(Coverage(Security(), Shipped().Single(r => r.Id == "SEC-002")));
    }

    [SqliteFact]
    public void A_narrowed_gap_is_reported_and_still_runs()
    {
        // Advisory means advisory. Adding a clause that excludes row 104 makes the predicate
        // demonstrably too narrow, and the plan is still OK with the remaining four rows
        // planned — a person reads the finding and decides.
        var rule = Shipped().Single(r => r.Id == "SEC-001");
        rule.Gap += " AND Rooms IS NOT NULL";

        var db = Security();
        var plan = new Planner(db, new LedgerRepository(db), new List<GapRule> { rule }, seed: 1)
                   .Plan("narrowed", "sha").Rules.Single();

        Assert.Equal(1, plan.Coverage!.UncoveredViolations);
        Assert.Equal(new[] { "104" }, plan.Coverage.UncoveredKeys);
        Assert.Equal("OK", plan.Status);
        Assert.Equal(4, plan.Patches.Count);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
