using SynthGen.Sqlite;

namespace SynthGen.Tests.Support;

/// <summary>
/// A test that needs the conda/micromamba-provided SQLite. Skips (with the probe's
/// explanation) instead of failing when no conda sqlite3 library is available.
/// </summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!SqliteNative.TryInitialize(out var error))
            Skip = error!;
    }
}
