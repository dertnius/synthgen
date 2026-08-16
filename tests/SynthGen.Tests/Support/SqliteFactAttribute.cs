using SynthGen.Core.Sqlite;

namespace SynthGen.Tests.Support;

/// <summary>
/// A test that needs the native SQLite library. Local developer runs may skip when the
/// library is not provisioned; CI must fail rather than silently reporting a green suite.
/// </summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!SqliteNative.TryInitialize(out var error))
        {
            var required = Environment.GetEnvironmentVariable("SYNTHGEN_REQUIRE_SQLITE") == "1" ||
                           string.Equals(Environment.GetEnvironmentVariable("CI"), "true",
                                         StringComparison.OrdinalIgnoreCase);
            if (required)
                throw new InvalidOperationException($"SQLite-backed tests are required in CI: {error}");
            Skip = error!;
        }
    }
}
