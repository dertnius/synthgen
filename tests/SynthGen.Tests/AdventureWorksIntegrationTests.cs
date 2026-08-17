using Dapper;
using SynthGen.Core.Ddl;
using SynthGen.Core.Eval;
using SynthGen.Core.Generation;
using SynthGen.Core.Load;
using SynthGen.Core.Rules;
using SynthGen.Core.Sqlite;
using SynthGen.Tests.Support;

namespace SynthGen.Tests;

/// <summary>
/// Real-world integration test: an 11-table AdventureWorks-compatible subset (Microsoft's
/// public SQL Server sample schema) generated and validated end-to-end on SQLite.
/// Exercises multi-schema attach (HumanResources + Person + Production + Sales),
/// cross-schema FK chains, a composite PK with IDENTITY and one without, computed-column
/// skipping, and CHECK-mirroring rules.
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
        "08-currency.rules.yaml",
        "09-employee.rules.yaml",
        "10-employeefinancials.rules.yaml",
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
        Assert.Equal(11, _tables.Count);
        Assert.Equal(
            new[] { "HumanResources", "Person", "Production", "Sales" },
            _tables.Select(t => t.Schema).Distinct().OrderBy(s => s, StringComparer.Ordinal));

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

        // Composite PK with no IDENTITY member: both halves come from the rules file.
        var financials = _tables.Single(t => t.Name == "EmployeeFinancials");
        Assert.Equal("HumanResources", financials.Schema);
        Assert.Equal(new[] { "BusinessEntityID", "EffectiveDate" }, financials.PrimaryKeyColumns);
        Assert.All(financials.PrimaryKeyColumns,
                   c => Assert.False(financials.FindColumn(c)!.IsIdentity));
        Assert.Equal(2, financials.ForeignKeys.Count);
        Assert.Equal(3, financials.CheckConstraints.Count);
        Assert.All(
            new[] { "BaseSalary", "BonusTarget", "PayFrequency" },
            c => Assert.Contains(financials.CheckConstraints,
                                 ck => ck.Name == $"CK_EmployeeFinancials_{c}"));
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
            var rulesPath = Path.Combine(_samplesDir, rulesFile);
            var rules = RulesLoader.LoadFile(rulesPath);
            var datasets = DatasetStore.ForRulesFile(rulesPath);
            var table = _tables.Single(t =>
                $"{t.Schema}.{t.Name}".Equals(rules.Table, StringComparison.OrdinalIgnoreCase));

            var plan = GenerationPlan.Build(table, rules, datasets);
            Assert.Empty(plan.Warnings);

            var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());
            var generator = new RowGenerator(plan, lookups, datasets);
            var loaded = new SqliteTableWriter(_factory).Load(generator).RowsLoaded;

            Assert.Equal(rules.Rows, loaded);
            Assert.Empty(generator.Truncations);
            totalRows += loaded;

            var results = evaluator.Run(rules.Evaluations);
            Assert.All(results, r => Assert.True(
                r.Passed, $"{rulesFile} / {r.Name}: value={r.Value} expected={r.Expected} ({r.Error})"));
        }

        Assert.Equal(1000 + 4 + 12 + 200 + 10 + 300 + 500 + 2000 + 24 + 400 + 250, totalRows);

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

        // The HR chain crosses three schemas in one hop: a compensation row resolves to an
        // employee, that employee to a person, and its currency to the Sales vocabulary.
        var orphanFinancials = check.ExecuteScalar<long>("""
            SELECT COUNT(*)
            FROM [HumanResources].[EmployeeFinancials] f
            LEFT JOIN [HumanResources].[Employee] e ON e.[BusinessEntityID] = f.[BusinessEntityID]
            LEFT JOIN [Person].[Person] p ON p.[PersonID] = e.[BusinessEntityID]
            LEFT JOIN [Sales].[Currency] c ON c.[CurrencyCode] = f.[CurrencyCode]
            WHERE e.[BusinessEntityID] IS NULL OR p.[PersonID] IS NULL OR c.[CurrencyCode] IS NULL
            """);
        Assert.Equal(0, orphanFinancials);
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
            var rulesPath = Path.Combine(_samplesDir, rulesFile);
            var rules = RulesLoader.LoadFile(rulesPath);
            var datasets = DatasetStore.ForRulesFile(rulesPath);
            var table = _tables.Single(t =>
                $"{t.Schema}.{t.Name}".Equals(rules.Table, StringComparison.OrdinalIgnoreCase));
            var plan = GenerationPlan.Build(table, rules, datasets);
            var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());
            new SqliteTableWriter(_factory).Load(new RowGenerator(plan, lookups, datasets));
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

        // Composite PK with no identity member: SynthGen enforces single-column uniqueness
        // only, so the rules file makes BusinessEntityID unique to keep the pair unique.
        Assert.Equal(0L, check.ExecuteScalar<long>("""
            SELECT COUNT(*) FROM (
                SELECT [BusinessEntityID], [EffectiveDate]
                FROM [HumanResources].[EmployeeFinancials]
                GROUP BY [BusinessEntityID], [EffectiveDate]
                HAVING COUNT(*) > 1
            ) dupes
            """));
    }
}
