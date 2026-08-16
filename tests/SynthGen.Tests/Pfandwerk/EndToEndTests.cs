using Dapper;
using Pfandwerk;
using SynthGen.Core.Sqlite;

namespace Pfandwerk.Tests;

/// <summary>
/// The definition of done, as a test. This was a shell script that nobody ran in CI; as a
/// test it guards the whole pipeline on every `dotnet test`.
///
/// <para>Runs the real phases in process against a throwaway SQLite fixture: no server, no
/// network, no Copilot. The two agent steps are narrative only and are not part of what is
/// asserted here.</para>
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-e2e").FullName;
    private readonly string _artifacts;
    private readonly string _fixture;

    public EndToEndTests()
    {
        _artifacts = Path.Combine(_dir, "artifacts");
        _fixture = Path.Combine(_dir, "e2e.db");
        Directory.CreateDirectory(_artifacts);
    }

    private static readonly List<GapRule> Rules =
        GapRulesLoader.LoadFile(GapRulesLoaderTests.FindRepoFile("rules/gaps.yaml")).Rules
            .Where(r => r.Table == "dbo.Security").ToList();

    private static readonly Check[] ConsumerChecks =
    {
        new("Security.EfhBathroomsNotNull",
            "SELECT COUNT(*) FROM dbo.Security WHERE PropertyType = 'EFH' AND Bathrooms IS NULL", 0),
        new("Security.SecurityIdPresent",
            "SELECT COUNT(*) FROM dbo.Security WHERE SecurityId IS NULL", 0),
        new("Security.RowCountStable", "SELECT COUNT(*) FROM dbo.Security", 8),
    };

    private DbContext Db() => new(Provider.Sqlite, _fixture, _fixture);
    private string P(string name) => Path.Combine(_artifacts, name);

    /// <summary>
    /// Seeds dbo.Security. Row 104 has a NULL room count on purpose: its bathrooms cannot
    /// be derived, so it stays broken and must come out as a pre-existing failure rather
    /// than a regression. Row 108 is already correct and must not be touched.
    /// </summary>
    private void BuildFixture()
    {
        var db = Db();
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
    }

    /// <summary>Plan, approve, apply, verify — the deterministic spine.</summary>
    private VerifyDocument RunOnce(int seed)
    {
        var db = Db();
        var ledger = new LedgerRepository(db);
        var runId = $"test-{seed}";

        var plan = new Planner(db, ledger, Rules, seed).Plan(runId, "rulessha");
        Json.Write(P("plan.json"), plan);

        var baseline = new BaselineDocument(runId,
            CheckRunner.Run(db, CheckRunner.Invariants(Rules)),
            CheckRunner.Run(db, ConsumerChecks));
        Json.Write(P("baseline.json"), baseline);

        Json.Write(P("plan.approved"),
            new Approval(Json.Sha256File(P("plan.json")), "test", "test@example.test",
                         DateTime.UtcNow.ToString("O")));

        new Patcher(new SqlPatchSink(db, "test"), Rules)
            .Apply(P("plan.json"), P("plan.approved"), P("patches.jsonl"));

        var verify = new Verifier(db, Rules).Verify(plan, baseline, ConsumerChecks);
        Json.Write(P("verify.json"), verify);
        return verify;
    }

    private List<(int PropertyId, string? SecurityId, int? Bathrooms)> Table()
    {
        using var conn = Db().OpenTarget();
        return conn.Query<(int, string?, int?)>(
            "SELECT PropertyId, SecurityId, Bathrooms FROM dbo.Security ORDER BY PropertyId").ToList();
    }

    [SqliteFact]
    public void The_definition_of_done()
    {
        BuildFixture();
        var before = Table();

        // --- run 1 -------------------------------------------------------------
        var verify = RunOnce(seed: 42);

        Assert.Equal(ExitCodes.Ok, Verifier.ExitCodeFor(verify));
        Assert.Empty(verify.Regressions);

        // Row 104 cannot be derived, so it is still failing — but it was failing before the
        // run too, which is exactly what the baseline exists to establish.
        Assert.Contains("SEC-001.invariant", verify.PreexistingReds);

        var afterRun = Table().ToDictionary(r => r.PropertyId);
        Assert.Equal(1, afterRun[101].Bathrooms);          // 3 rooms
        Assert.Equal(2, afterRun[102].Bathrooms);          // 5 rooms
        Assert.Equal(3, afterRun[103].Bathrooms);          // 7 rooms
        Assert.Null(afterRun[104].Bathrooms);              // Rooms NULL -> skipped, not guessed
        Assert.Equal(3, afterRun[105].Bathrooms);          // 9 rooms
        Assert.Equal(2, afterRun[108].Bathrooms);          // already correct, untouched

        var mintedIds = new[] { afterRun[102].SecurityId, afterRun[104].SecurityId };
        Assert.All(mintedIds, id => Assert.NotNull(id));

        var facts = FactExtractor.Extract(P("patches.jsonl"), P("verify.json"),
                                          P("plan.json"), P("plan.approved"));
        Assert.Equal(6, facts.Rules.Sum(r => r.Patched));
        Assert.Equal(2, facts.NewIdentities.Count);

        // The fallback report must survive its own audit; it is what ships when the
        // narrative fails.
        File.WriteAllText(P("report.md"), ReportAuditor.Fallback(facts));
        Json.Write(P("facts.json"), facts);
        Assert.Equal("pass", ReportAuditor.Audit(P("report.md"), P("facts.json")).Verdict);

        // --- revert ------------------------------------------------------------
        var ledgerBefore = new LedgerRepository(Db()).Count();
        Assert.Equal(2, ledgerBefore);

        new Reverter(new SqlPatchSink(Db(), "test"), Rules).Revert(P("patches.jsonl"));

        Assert.Equal(before, Table());
        Assert.Equal(2, new LedgerRepository(Db()).Count());   // ledger rows survive a revert

        // --- run 2, different seed --------------------------------------------
        RunOnce(seed: 999);

        var afterSecond = Table().ToDictionary(r => r.PropertyId);
        Assert.Equal(mintedIds[0], afterSecond[102].SecurityId);
        Assert.Equal(mintedIds[1], afterSecond[104].SecurityId);
    }

    [SqliteFact]
    public void A_plan_edited_after_approval_is_refused()
    {
        BuildFixture();
        var db = Db();
        var plan = new Planner(db, new LedgerRepository(db), Rules, 42).Plan("tamper", "sha");
        Json.Write(P("plan.json"), plan);
        Json.Write(P("plan.approved"),
            new Approval(Json.Sha256File(P("plan.json")), "u", "u@example.test", "2026-08-15T00:00:00Z"));

        // Same shape, different run id — enough to change the hash.
        Json.Write(P("plan.json"), plan with { RunId = "tampered" });

        var ex = Assert.Throws<PatchAbortedException>(() =>
            new Patcher(new SqlPatchSink(db, "test"), Rules)
                .Apply(P("plan.json"), P("plan.approved"), P("patches.jsonl")));
        Assert.Contains("hash mismatch", ex.Message);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
