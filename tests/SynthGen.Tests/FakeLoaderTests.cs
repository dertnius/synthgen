using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;
using SynthGen.Core.Rules;
using SynthGen.Tests.Support;

namespace SynthGen.Tests;

/// <summary>Verifies the generation-to-loader contract through the ITableLoader seam.</summary>
public class FakeLoaderTests
{
    private const string Ddl = """
        CREATE TABLE [dbo].[T]
        (
            [Id]   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            [Name] NVARCHAR(30) NOT NULL,
            [Age]  INT NULL
        );
        """;

    [Fact]
    public void Loader_receives_planned_columns_rows_and_flags()
    {
        var rules = RulesLoader.Load("""
            rows: 25
            seed: 11
            truncateBeforeLoad: true
            columns:
              Name: { strategy: faker, method: name.firstName }
              Age:  { strategy: int, min: 1, max: 99, nullRate: 0.2 }
            """);
        var plan = GenerationPlan.Build(DdlParser.ParseSingle(Ddl), rules);
        var generator = new RowGenerator(plan);
        var fake = new FakeTableLoader();

        var result = fake.Load(generator, rules.TruncateBeforeLoad);

        Assert.Equal(25, result.RowsLoaded);
        Assert.Equal(25, fake.Rows.Count);
        Assert.Equal("[dbo].[T]", fake.TableName);
        Assert.True(fake.TruncateRequested);
        Assert.False(fake.KeepIdentity);
        // Identity column excluded; generated columns arrive in plan order.
        Assert.Equal(new[] { "Name", "Age" }, fake.ColumnNames);
        Assert.All(fake.Rows, r => Assert.Equal(2, r.Length));
        Assert.All(fake.Rows, r => Assert.IsType<string>(r[0]));
    }
}
