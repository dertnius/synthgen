using SynthGen.Core.Generation;

namespace SynthGen.Core.Load;

/// <summary>
/// Destination for generated rows. Production uses <see cref="BulkLoader"/> (SqlBulkCopy);
/// tests use fakes or the SQLite writer for fully local runs.
/// </summary>
public interface ITableLoader
{
    LoadResult Load(RowGenerator generator, bool truncateFirst = false);
}
