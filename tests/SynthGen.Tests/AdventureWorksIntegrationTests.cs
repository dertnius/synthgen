using Dapper;
using SynthGen.Core.Ddl;
using SynthGen.Core.Eval;
using SynthGen.Core.Generation;
using SynthGen.Core.Load;
using SynthGen.Core.Rules;
using SynthGen.Sqlite;
using SynthGen.Tests.Support;

namespace SynthGen.Tests;

/// <summary>
/// Real-world integration test: an 8-table AdventureWorks-compatible subset (Microsoft's
/// public SQL Server sample schema) generated and validated end-to-end on SQLite.
/// Exercises multi-schema attach (Person + Production + Sales), cross-schema FK chains, a
/// composite PK with IDENTITY, computed-column skipping, and CHECK-mirroring rules.
/// </summary>
public sealed class AdventureWorksIntegrationTests : IDisposable
{
    private static readonly string[] RulesInOrder =
    {
        "00-person.rules.yaml",
        "01-productcategory.rules.yaml",
        "02-productsubcategory.rules.yaml",
        "03-product.rules.yaml",
        "04-salesterritory.rules.yaml",
        "05-customer.rules.yaml",
        "06-salesorderheader.rules.yaml",
        "07-salesorderdetail.rules.yaml",
    };

    private readonly string _dir;
    private readonly string _samplesDir;
    private readonly SqliteConnectionFactory _factory;
    private readonly List<Core.Model.TableDefinition> _tables;

    public AdventureWorksIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "synthgen-aw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _samplesDir = Path.Combine(RulesTests.FindRepoRoot(), "samples", "adventureworks");

        var ddl = File.ReadAllText(Path.Combine(_samplesDir, "adventureworks.sql"));
        _tables = DdlParser.ParseScript(ddl);
        _factory = new SqliteConnectionFactory(
            Path.Combine(_dir, "aw.db"),
            _tables.Select(t => t.Schema));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Ddl_parses_with_expected_shape()
    {
        Assert.Equal(8, _tables.Count);
        Assert.Equal(
            new[] { "Person", "Production", "Sales" },
            _tables.Select(t => t.Schema).Distinct().OrderBy(s => s));

        var detail = _tables.Single(t => t.Name == "SalesOrderDetail");
        Assert.Equal(new[] { "SalesOrderID", "SalesOrderDetailID" }, detail.PrimaryKeyColumns);
        Assert.True(detail.FindColumn("SalesOrderDetailID")!.IsIdentity);
        Assert.True(detail.FindColumn("LineTotal")!.IsComputed);
        Assert.Equal(2, detail.ForeignKeys.Count);

        var header = _tables.Single(t => t.Name == "SalesOrderHeader");
        Assert.True(header.FindColumn("TotalDue")!.IsComputed);
        Assert.Contains(header.CheckConstraints, c => c.Name == "CK_SalesOrderHeader_Status");

        // Reserved-word column parsed correctly.
        var territory = _tables.Single(t => t.Name == "SalesTerritory");
        Assert.NotNull(territory.FindColumn("Group"));
    }

    [SqliteFact]
    public void Full_chain_loads_and_every_evaluation_passes()
    {
        using (var conn = _factory.Open())
        {
            foreach (var table in _tables)
                conn.Execute(SqliteSchemaBuilder.BuildCreateTable(table));
        }

        var evaluator = new Evaluator(() => _factory.Open());
        long totalRows = 0;

        foreach (var rulesFile in RulesInOrder)
        {
            var rules = RulesLoader.LoadFile(Path.Combine(_samplesDir, rulesFile));
            var table = _tables.Single(t =>
                $"{t.Schema}.{t.Name}".Equals(rules.Table, StringComparison.OrdinalIgnoreCase));

            var plan = GenerationPlan.Build(table, rules);
            Assert.Empty(plan.Warnings);

            var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());
            var generator = new RowGenerator(plan, lookups);
            var loaded = new SqliteTableWriter(_factory).Load(generator).RowsLoaded;

            Assert.Equal(rules.Rows, loaded);
            Assert.Empty(generator.Truncations);
            totalRows += loaded;

            var results = evaluator.Run(rules.Evaluations);
            Assert.All(results, r => Assert.True(
                r.Passed, $"{rulesFile} / {r.Name}: value={r.Value} expected={r.Expected} ({r.Error})"));
        }

        Assert.Equal(1000 + 4 + 12 + 200 + 10 + 300 + 500 + 2000, totalRows);

        // Cross-table sanity beyond the per-table evaluations: order lines join back
        // through header AND product across schemas in one query.
        using var check = _factory.Open();
        var orphanJoins = check.ExecuteScalar<long>("""
            SELECT COUNT(*)
            FROM [Sales].[SalesOrderDetail] d
            LEFT JOIN [Sales].[SalesOrderHeader] h ON h.[SalesOrderID] = d.[SalesOrderID]
            LEFT JOIN [Production].[Product] p ON p.[ProductID] = d.[ProductID]
            WHERE h.[SalesOrderID] IS NULL OR p.[ProductID] IS NULL
            """);
        Assert.Equal(0, orphanJoins);
        var orphanPeople = check.ExecuteScalar<long>("""
            SELECT COUNT(*)
            FROM [Sales].[Customer] c
            LEFT JOIN [Person].[Person] p ON p.[PersonID] = c.[PersonID]
            WHERE c.[PersonID] IS NOT NULL AND p.[PersonID] IS NULL
            """);
        Assert.Equal(0, orphanPeople);
    }

    [SqliteFact]
    public void Identity_and_composite_pk_behave_like_sql_server()
    {
        using (var conn = _factory.Open())
        {
            foreach (var table in _tables)
                conn.Execute(SqliteSchemaBuilder.BuildCreateTable(table));
        }

        foreach (var rulesFile in RulesInOrder)
        {
            var rules = RulesLoader.LoadFile(Path.Combine(_samplesDir, rulesFile));
            var table = _tables.Single(t =>
                $"{t.Schema}.{t.Name}".Equals(rules.Table, StringComparison.OrdinalIgnoreCase));
            var plan = GenerationPlan.Build(table, rules);
            var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());
            new SqliteTableWriter(_factory).Load(new RowGenerator(plan, lookups));
        }

        using var check = _factory.Open();

        // Single-column identity: database-assigned, dense from 1.
        Assert.Equal(1L, check.ExecuteScalar<long>(
            "SELECT MIN([ProductID]) FROM [Production].[Product]"));
        Assert.Equal(200L, check.ExecuteScalar<long>(
            "SELECT MAX([ProductID]) FROM [Production].[Product]"));

        // Composite-PK identity member: supplied by the sequence rule, globally unique.
        Assert.Equal(2000L, check.ExecuteScalar<long>(
            "SELECT COUNT(DISTINCT [SalesOrderDetailID]) FROM [Sales].[SalesOrderDetail]"));
    }
}
