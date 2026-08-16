using Dapper;
using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>
/// The survey feeds the rule-drafting agent, so a survey that silently reports nothing is
/// worse than one that fails — the agent would draft confidently from an empty picture.
/// These tests exist because exactly that happened: a broad catch hid a materialisation
/// error and every column came back with no value distribution at all.
/// </summary>
public sealed class SurveyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-survey").FullName;

    private DbContext Db()
    {
        var path = Path.Combine(_dir, "survey.db");
        return new DbContext(Provider.Sqlite, path, path);
    }

    /// <summary>
    /// 20 houses. EnergyClass is 25% NULL with a domain sentinel among otherwise even
    /// values; Legacy is dominated by '0'; Note is nullable but never NULL; MiddleName is
    /// genuinely optional.
    /// </summary>
    private DbContext Seed()
    {
        var db = Db();
        using var conn = db.OpenTarget();
        conn.Execute("""
            CREATE TABLE dbo.House (
                HouseId INTEGER NOT NULL PRIMARY KEY,
                EnergyClass TEXT NULL, Legacy TEXT NULL, Note TEXT NULL, MiddleName TEXT NULL)
            """);
        for (var i = 1; i <= 20; i++)
        {
            conn.Execute("INSERT INTO dbo.House VALUES (@id, @e, @l, @n, @m)", new
            {
                id = i,
                e = i % 4 == 0 ? null : i % 5 == 0 ? "X9" : new[] { "A", "B", "C" }[i % 3],
                l = i <= 15 ? "0" : "live",
                n = "always here",
                m = i % 2 == 0 ? "Jo" : null,
            });
        }
        return db;
    }

    private ColumnSurvey Column(string name)
    {
        var survey = new Surveyor(Seed(), new List<GapRule>()).Survey(null);
        return survey.Tables.Single(t => t.Table == "dbo.House").Columns.Single(c => c.Name == name);
    }

    [SqliteFact]
    public void Counts_nulls_and_reports_the_rate()
    {
        var c = Column("EnergyClass");
        Assert.Equal(5, c.Nulls);                       // 4, 8, 12, 16, 20
        Assert.Equal(0.25, c.NullRate);
        Assert.Contains("25% NULL", c.Signals);
    }

    [SqliteFact]
    public void Reports_the_value_distribution()
    {
        // The regression that matters: this came back empty for every column because
        // SQLite returns COUNT(*) as Int64 and the record wanted an int.
        var c = Column("EnergyClass");
        Assert.NotEmpty(c.TopValues);
        Assert.Contains(c.TopValues, v => v.Value == "X9");
        Assert.Equal(15, c.TopValues.Sum(v => v.Count));   // every non-NULL row accounted for
    }

    [SqliteFact]
    public void Flags_a_dominant_value()
    {
        var c = Column("Legacy");
        Assert.Contains(c.Signals, s => s.Contains("'0'") && s.Contains("sentinel"));
    }

    [SqliteFact]
    public void Does_not_narrate_an_even_distribution()
    {
        // A flat spread is not a defect. Listing every value would bury the real signals.
        var c = Column("EnergyClass");
        Assert.DoesNotContain(c.Signals, s => s.Contains("'A'") || s.Contains("'B'"));
    }

    [SqliteFact]
    public void Flags_a_column_that_is_nullable_but_never_null()
    {
        // The useful inverse: the schema permits NULL, the data never has one, so a NULL
        // turning up later is an anomaly worth a rule.
        Assert.Contains("nullable but never NULL", Column("Note").Signals);
    }

    [SqliteFact]
    public void Says_nothing_about_the_primary_key()
    {
        // SQLite reports INTEGER PRIMARY KEY as nullable, which would flag every table's
        // key as interesting. A key is never a repair candidate.
        var c = Column("HouseId");
        Assert.True(c.IsKey);
        Assert.Empty(c.Signals);
    }

    [SqliteFact]
    public void Marks_columns_an_existing_rule_already_covers()
    {
        var rule = new GapRule
        {
            Id = "HOU-001", Table = "dbo.House", Key = "HouseId", Column = "EnergyClass",
            Kind = "ephemeral", Gap = "EnergyClass IS NULL", Fix = "dataset.energy-classes",
            Threshold = 10, Reason = "r",
        };
        var survey = new Surveyor(Seed(), new List<GapRule> { rule }).Survey(null);
        var columns = survey.Tables.Single().Columns;

        Assert.Equal("HOU-001", columns.Single(c => c.Name == "EnergyClass").CoveredByRule);
        Assert.Null(columns.Single(c => c.Name == "Legacy").CoveredByRule);
    }

    [SqliteFact]
    public void Writes_nothing_to_the_database()
    {
        var db = Seed();
        using var before = db.OpenTarget();
        var checksum = before.ExecuteScalar<string>(
            "SELECT group_concat(HouseId || ':' || COALESCE(EnergyClass,'~')) FROM dbo.House");

        new Surveyor(db, new List<GapRule>()).Survey(null);

        using var after = db.OpenTarget();
        Assert.Equal(checksum, after.ExecuteScalar<string>(
            "SELECT group_concat(HouseId || ':' || COALESCE(EnergyClass,'~')) FROM dbo.House"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
