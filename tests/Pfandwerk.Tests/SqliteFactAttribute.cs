using SynthGen.Sqlite;

namespace Pfandwerk.Tests;

/// <summary>
/// A test needing the native SQLite library. Skips with the probe's explanation instead of
/// failing when none is available — mirrors SynthGen.Tests' attribute of the same name.
/// </summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!SqliteNative.TryInitialize(out var error)) Skip = error!;
    }
}
