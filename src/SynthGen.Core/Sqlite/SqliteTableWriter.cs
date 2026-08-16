using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SynthGen.Core.Generation;
using SynthGen.Core.Load;

namespace SynthGen.Core.Sqlite;

/// <summary>
/// <see cref="ITableLoader"/> for SQLite: prepared single-row INSERTs inside one
/// transaction. Not SqlBulkCopy-fast, but plenty for local rule smoke tests, and it
/// exercises the identical generation pipeline.
/// </summary>
public sealed class SqliteTableWriter : ITableLoader
{
    private readonly SqliteConnectionFactory _factory;
    public Action<long>? OnBatchLoaded { get; set; }

    public SqliteTableWriter(SqliteConnectionFactory factory) => _factory = factory;

    public LoadResult Load(RowGenerator generator, bool truncateFirst = false)
    {
        var start = Stopwatch.StartNew();
        using var conn = _factory.Open();

        if (truncateFirst)
        {
            using var delete = conn.CreateCommand();
            delete.CommandText = $"DELETE FROM {generator.TableName}";
            delete.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;

        var names = generator.Columns.Select(c => $"[{c.Column.Name}]");
        var parameters = new SqliteParameter[generator.Columns.Count];
        for (int i = 0; i < parameters.Length; i++)
        {
            parameters[i] = insert.CreateParameter();
            parameters[i].ParameterName = $"$p{i}";
            insert.Parameters.Add(parameters[i]);
        }
        insert.CommandText =
            $"INSERT INTO {generator.TableName} ({string.Join(", ", names)}) " +
            $"VALUES ({string.Join(", ", parameters.Select(p => p.ParameterName))})";

        long total = 0;
        foreach (var row in generator.Rows())
        {
            for (int i = 0; i < row.Length; i++)
                parameters[i].Value = Adapt(row[i]);
            insert.ExecuteNonQuery();

            if (++total % generator.BatchSize == 0)
                OnBatchLoaded?.Invoke(total);
        }

        tx.Commit();
        if (total % generator.BatchSize != 0)
            OnBatchLoaded?.Invoke(total);

        return new LoadResult { RowsLoaded = total, Elapsed = start.Elapsed };
    }

    /// <summary>
    /// SQLite-friendly representations: decimals become REAL so numeric aggregates in
    /// evaluation queries work; GUIDs become canonical strings for stable DISTINCT.
    /// </summary>
    private static object Adapt(object? value) => value switch
    {
        null => DBNull.Value,
        decimal d => (double)d,
        Guid g => g.ToString("D"),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => value,
    };
}
