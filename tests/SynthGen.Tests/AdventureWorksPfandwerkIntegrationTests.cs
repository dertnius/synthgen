using Dapper;
using Pfandwerk;
using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;
using SynthGen.Core.Load;
using SynthGen.Core.Rules;
using SynthGen.Sqlite;

namespace SynthGen.Tests;

/// <summary>
/// Exercises the AdventureWorks sample as a real pfandwerk run, including fixture
/// corruption, the approved-plan gate, postconditions, report audit and identity reuse.
/// </summary>
public sealed class AdventureWorksPfandwerkIntegrationTests : IDisposable
{
    private static readonly string[] GenerationRules =
    {
        "01-productcategory.rules.yaml",
        "02-productsubcategory.rules.yaml",
        "03-product.rules.yaml",
        "04-salesterritory.rules.yaml",
        "05-customer.rules.yaml",
        "06-salesorderheader.rules.yaml",
        "07-salesorderdetail.rules.yaml",
    };

    private readonly string _root = RulesTests.FindRepoRoot();
    private readonly string _dir;
    private readonly string _database;
    private readonly string _artifacts;
    private readonly string _sample;
    private readonly SqliteConnectionFactory _factory;
    private readonly List<SynthGen.Core.Model.TableDefinition> _tables;
    private readonly List<GapRule> _gapRules;

    public AdventureWorksPfandwerkIntegrationTests()
    {
        _dir = Path.Combine(_root, "scratch", "aw-pfandwerk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _database = Path.Combine(_dir, "adventureworks.db");
        _artifacts = Path.Combine(_dir, "artifacts");
        _sample = Path.Combine(_root, "samples", "adventureworks");

        _tables = DdlParser.ParseScript(File.ReadAllText(Path.Combine(_sample, "adventureworks.sql")));
        _factory = new SqliteConnectionFactory(_database, _tables.Select(t => t.Schema));
        _gapRules = GapRulesLoader.LoadFile(Path.Combine(_sample, "gaps.yaml")).Rules;
    }

    [SqliteFact]
    public void Full_sample_run_repairs_corruption_and_reuses_identities()
    {
        GenerateSample();
        CorruptSample();
        var db = new DbContext(Provider.Sqlite, _database, _database);
        var ledger = new LedgerRepository(db);
        ledger.EnsureCreated();

        var first = RunPatch(db, ledger, "first", 4242);
        Assert.Equal(ExitCodes.Ok, Verifier.ExitCodeFor(first));
        Assert.Empty(first.Regressions);
        AssertPostconditions();

        var firstIds = CustomerIdentityValues(ledger);
        Assert.Equal(3, firstIds.Count);
        var ledgerCount = ledger.Count();

        // Reintroduce the same defects after the first run. Planner must read the
        // append-only ledger and produce the exact same values with a new seed.
        using (var conn = db.OpenTarget())
            conn.Execute("UPDATE [Sales].[Customer] SET [PersonID] = NULL WHERE [CustomerID] IN (1, 2, 3)");

        var second = RunPatch(db, ledger, "second", 9999);
        Assert.Equal(ExitCodes.Ok, Verifier.ExitCodeFor(second));
        Assert.Equal(ledgerCount, ledger.Count());
        var secondIds = CustomerIdentityValues(ledger);
        foreach (var key in firstIds.Keys)
            Assert.Equal(firstIds[key], secondIds[key]);
        AssertPostconditions();

        var facts = FactExtractor.Extract(P("patches.jsonl"), P("verify.json"),
                                          P("plan.json"), P("plan.approved"));
        Json.Write(P("facts.json"), facts);
        File.WriteAllText(P("report.md"), ReportAuditor.Fallback(facts));
        var audit = ReportAuditor.Audit(P("report.md"), P("facts.json"));
        Json.Write(P("report.audit.json"), audit);
        Assert.Equal("pass", audit.Verdict);
    }

    private void GenerateSample()
    {
        using (var conn = _factory.Open())
            foreach (var table in _tables)
                conn.Execute(SqliteSchemaBuilder.BuildCreateTable(table));

        foreach (var file in GenerationRules)
        {
            var rules = SynthGen.Core.Rules.RulesLoader.LoadFile(Path.Combine(_sample, file));
            var table = _tables.Single(t =>
                $"{t.Schema}.{t.Name}".Equals(rules.Table, StringComparison.OrdinalIgnoreCase));
            var plan = GenerationPlan.Build(table, rules);
            var lookups = LookupFetcher.Fetch(plan, _factory.Open);
            var loaded = new SqliteTableWriter(_factory).Load(new RowGenerator(plan, lookups));
            Assert.Equal(rules.Rows, loaded.RowsLoaded);
        }
    }

    private void CorruptSample()
    {
        using var conn = _factory.Open();
        conn.Execute("""
            UPDATE [Production].[Product] SET [Color] = 'X9' WHERE [ProductID] IN (1, 2, 3);
            UPDATE [Sales].[SalesOrderHeader] SET [Status] = 0 WHERE [SalesOrderID] IN (1, 2, 3);
            UPDATE [Sales].[Customer] SET [PersonID] = NULL WHERE [CustomerID] IN (1, 2, 3);
            """);
    }

    private VerifyDocument RunPatch(DbContext db, LedgerRepository ledger, string runId, int seed)
    {
        Directory.CreateDirectory(_artifacts);
        var plan = new Planner(db, ledger, _gapRules, seed).Plan(runId, "sample-rules");
        Json.Write(P("plan.json"), plan);
        var baseline = new BaselineDocument(runId,
            CheckRunner.Run(db, CheckRunner.Invariants(_gapRules)),
            CheckRunner.Run(db, ConsumerChecks()));
        Json.Write(P("baseline.json"), baseline);
        Json.Write(P("plan.approved"),
            new Approval(Json.Sha256File(P("plan.json")), "test", "test@example.test",
                         DateTime.UtcNow.ToString("O")));

        new Patcher(new SqlPatchSink(db, "test"), _gapRules)
            .Apply(P("plan.json"), P("plan.approved"), P("patches.jsonl"));
        var verify = new Verifier(db, _gapRules).Verify(plan, baseline, ConsumerChecks());
        Json.Write(P("verify.json"), verify);
        return verify;
    }

    private Dictionary<string, string> CustomerIdentityValues(LedgerRepository ledger) =>
        new[] { "1", "2", "3" }.ToDictionary(
            key => key,
            key => ledger.Lookup("Sales.Customer", key, "PersonID")
                   ?? throw new Xunit.Sdk.XunitException($"missing identity for customer {key}"));

    private void AssertPostconditions()
    {
        using var conn = _factory.Open();
        Assert.Equal(0, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM [Production].[Product] WHERE [Color] = 'X9'"));
        Assert.Equal(0, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM [Sales].[SalesOrderHeader] WHERE [Status] < 1 OR [Status] > 5"));
        Assert.Equal(0, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM [Sales].[Customer] WHERE [PersonID] IS NULL"));
    }

    private List<Check> ConsumerChecks() =>
    [
        new("AdventureWorks.ProductColorSentinelAbsent",
            "SELECT COUNT(*) FROM Production.Product WHERE Color = 'X9'", 0),
        new("AdventureWorks.OrderStatusContract",
            "SELECT COUNT(*) FROM Sales.SalesOrderHeader WHERE Status < 1 OR Status > 5", 0),
        new("AdventureWorks.CustomerPersonPresent",
            "SELECT COUNT(*) FROM Sales.Customer WHERE PersonID IS NULL", 0),
        new("AdventureWorks.ProductRowCountStable",
            "SELECT COUNT(*) FROM Production.Product", 200),
        new("AdventureWorks.CustomerRowCountStable",
            "SELECT COUNT(*) FROM Sales.Customer", 300),
    ];

    private string P(string file) => Path.Combine(_artifacts, file);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
