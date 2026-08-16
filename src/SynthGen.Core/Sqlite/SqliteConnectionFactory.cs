using Microsoft.Data.Sqlite;

namespace SynthGen.Sqlite;

/// <summary>
/// Opens SQLite connections against a file database, attaching one companion database
/// per SQL Server schema (e.g. "test.db" + "test.dbo.db" attached AS dbo). MSSQL-style
/// queries like SELECT ... FROM [dbo].[Customers] then work without rewriting.
/// In-memory databases are unsupported on purpose: the pipeline opens several
/// connections (load, lookups, evaluate) that must observe the same data.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;
    private readonly IReadOnlyList<string> _schemas;

    public SqliteConnectionFactory(string databasePath, IEnumerable<string>? schemas = null)
    {
        if (databasePath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "In-memory SQLite databases are not supported; use a file path.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);
        _schemas = (schemas ?? new[] { "dbo" })
            .Where(s => !string.Equals(s, "main", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string DatabasePath => _databasePath;

    /// <summary>File that backs a given schema's attached database.</summary>
    public string SchemaFile(string schema) =>
        Path.Combine(
            Path.GetDirectoryName(_databasePath)!,
            $"{Path.GetFileNameWithoutExtension(_databasePath)}.{schema}{Path.GetExtension(_databasePath)}");

    public SqliteConnection Open()
    {
        SqliteNative.Initialize();

        // SQLite creates the database file but never its directory; without this a
        // fresh checkout fails with an opaque "unable to open database file".
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        // Pooling off: pooled connections would come back with schemas already
        // attached (ATTACH is per physical connection) and re-attaching throws.
        var conn = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        conn.Open();

        foreach (var schema in _schemas)
        {
            using var attach = conn.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $file AS " + Quote(schema);
            attach.Parameters.AddWithValue("$file", SchemaFile(schema));
            attach.ExecuteNonQuery();
        }
        return conn;
    }

    /// <summary>Deletes the main and schema database files (test cleanup).</summary>
    public void DeleteFiles()
    {
        foreach (var file in new[] { _databasePath }.Concat(_schemas.Select(SchemaFile)))
            if (File.Exists(file)) File.Delete(file);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
