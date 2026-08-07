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
/// End-to-end pipeline tests against a real (conda-provisioned) SQLite database:
/// DDL -> schema -> generate -> load -> FK lookups -> evaluations. These are the local
/// stand-in for the SqlBulkCopy path, which needs a real SQL Server.
/// </summary>
public sealed class SqliteIntegrationTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteConnectionFactory _factory;

    public SqliteIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "synthgen-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _factory = new SqliteConnectionFactory(Path.Combine(_dir, "test.db"), new[] { "dbo" });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (List<Core.Model.TableDefinition> tables, string samplesDir) LoadSamples()
    {
        var root = RulesTests.FindRepoRoot();
        var samplesDir = Path.Combine(root, "samples");
        var ddl = File.ReadAllText(Path.Combine(samplesDir, "customers.sql"));
        return (DdlParser.ParseScript(ddl), samplesDir);
    }

    private void CreateSchema(IEnumerable<Core.Model.TableDefinition> tables)
    {
        using var conn = _factory.Open();
        foreach (var table in tables)
            conn.Execute(SqliteSchemaBuilder.BuildCreateTable(table));
    }

    private long LoadTable(
        Core.Model.TableDefinition table, string rulesPath, bool truncateFirst = false)
    {
        var rules = RulesLoader.LoadFile(rulesPath);
        var plan = GenerationPlan.Build(table, rules);
        var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());
        var generator = new RowGenerator(plan, lookups);
        var writer = new SqliteTableWriter(_factory);
        return writer.Load(generator, truncateFirst).RowsLoaded;
    }

    [SqliteFact]
    public void Full_pipeline_countries_then_customers_all_evaluations_pass()
    {
        var (tables, samplesDir) = LoadSamples();
        CreateSchema(tables);

        var countries = tables.Single(t => t.Name == "Countries");
        var customers = tables.Single(t => t.Name == "Customers");

        Assert.Equal(12, LoadTable(countries, Path.Combine(samplesDir, "countries.rules.yaml")));
        Assert.Equal(1000, LoadTable(customers, Path.Combine(samplesDir, "customers.rules.yaml")));

        var evaluator = new Evaluator(() => _factory.Open());

        var countryResults = evaluator.Run(
            RulesLoader.LoadFile(Path.Combine(samplesDir, "countries.rules.yaml")).Evaluations);
        Assert.All(countryResults, r => Assert.True(r.Passed, $"{r.Name}: {r.Value} ({r.Error})"));

        var customerResults = evaluator.Run(
            RulesLoader.LoadFile(Path.Combine(samplesDir, "customers.rules.yaml")).Evaluations);
        Assert.All(customerResults, r => Assert.True(r.Passed, $"{r.Name}: {r.Value} ({r.Error})"));

        // The informational avg-credit-limit evaluation reports a numeric value.
        var avg = customerResults.Single(r => r.Name == "avg-credit-limit");
        Assert.True(avg.Informational);
        Assert.IsType<double>(avg.Value);
    }

    [SqliteFact]
    public void Identity_pk_is_assigned_by_the_database()
    {
        var (tables, samplesDir) = LoadSamples();
        CreateSchema(tables);
        var countries = tables.Single(t => t.Name == "Countries");
        var customers = tables.Single(t => t.Name == "Customers");
        LoadTable(countries, Path.Combine(samplesDir, "countries.rules.yaml"));
        LoadTable(customers, Path.Combine(samplesDir, "customers.rules.yaml"));

        using var conn = _factory.Open();
        var ids = conn.Query<long>("SELECT [CustomerId] FROM [dbo].[Customers] ORDER BY [CustomerId]").ToList();
        Assert.Equal(1000, ids.Count);
        Assert.Equal(1000, ids.Distinct().Count());
        Assert.Equal(1, ids[0]); // INTEGER PRIMARY KEY starts at 1, like IDENTITY(1,1)
    }

    [SqliteFact]
    public void Truncate_then_reload_keeps_counts_exact()
    {
        var (tables, samplesDir) = LoadSamples();
        CreateSchema(tables);
        var countries = tables.Single(t => t.Name == "Countries");
        var rulesPath = Path.Combine(samplesDir, "countries.rules.yaml");

        LoadTable(countries, rulesPath);
        LoadTable(countries, rulesPath, truncateFirst: true);

        using var conn = _factory.Open();
        Assert.Equal(12, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM [dbo].[Countries]"));
    }

    [SqliteFact]
    public void Evaluator_surfaces_query_errors_instead_of_throwing()
    {
        var (tables, _) = LoadSamples();
        CreateSchema(tables);

        var evaluator = new Evaluator(() => _factory.Open());
        var results = evaluator.Run(new[]
        {
            new Evaluation
            {
                Name = "broken",
                Query = "SELECT COUNT(*) FROM [dbo].[DoesNotExist]",
                Expect = new Expectation { EqualsValue = "0" },
            },
        });

        var r = Assert.Single(results);
        Assert.False(r.Passed);
        Assert.Contains("query failed", r.Error);
    }

    [SqliteFact]
    public void Lookup_fetcher_reads_from_sqlite()
    {
        var (tables, samplesDir) = LoadSamples();
        CreateSchema(tables);
        var countries = tables.Single(t => t.Name == "Countries");
        var customers = tables.Single(t => t.Name == "Customers");
        LoadTable(countries, Path.Combine(samplesDir, "countries.rules.yaml"));

        var rules = RulesLoader.LoadFile(Path.Combine(samplesDir, "customers.rules.yaml"));
        var plan = GenerationPlan.Build(customers, rules);
        var lookups = LookupFetcher.Fetch(plan, () => _factory.Open());

        Assert.Equal(12, lookups["CountryCode"].Count);

        // An empty parent table must fail with guidance, not generate orphan rows.
        using (var conn = _factory.Open())
            conn.Execute("DELETE FROM [dbo].[Countries]");
        var empty = LookupFetcher.Fetch(plan, () => _factory.Open());
        var ex = Assert.Throws<GenerationException>(() => new RowGenerator(plan, empty));
        Assert.Contains("no rows", ex.Message);
    }
}
