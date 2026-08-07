using SynthGen.Core.Generation;
using SynthGen.Core.Load;

namespace SynthGen.Tests.Support;

/// <summary>
/// Hand-rolled fake for <see cref="ITableLoader"/>: captures everything the loader
/// would send to the database so tests can assert on the wiring without a server.
/// </summary>
public sealed class FakeTableLoader : ITableLoader
{
    public List<object?[]> Rows { get; } = new();
    public List<string> ColumnNames { get; } = new();
    public string? TableName { get; private set; }
    public bool TruncateRequested { get; private set; }
    public bool KeepIdentity { get; private set; }

    public LoadResult Load(RowGenerator generator, bool truncateFirst = false)
    {
        TableName = generator.TableName;
        TruncateRequested = truncateFirst;
        KeepIdentity = generator.KeepIdentity;
        ColumnNames.AddRange(generator.Columns.Select(c => c.Column.Name));
        Rows.AddRange(generator.Rows());
        return new LoadResult { RowsLoaded = Rows.Count, Elapsed = TimeSpan.Zero };
    }
}
