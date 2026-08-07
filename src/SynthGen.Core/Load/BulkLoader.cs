using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using SynthGen.Core.Generation;

namespace SynthGen.Core.Load;

public sealed class LoadResult
{
    public long RowsLoaded { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Streams generated rows into SQL Server with SqlBulkCopy, batching through a reused
/// DataTable. Only planned columns are mapped, so identity/computed/dbDefault columns
/// keep their database-generated values.
/// </summary>
public sealed class BulkLoader : ITableLoader
{
    private readonly string _connectionString;
    public Action<long>? OnBatchLoaded { get; set; }

    public BulkLoader(string connectionString) => _connectionString = connectionString;

    public LoadResult Load(RowGenerator generator, bool truncateFirst = false)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();

        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        if (truncateFirst)
            ClearTable(conn, generator.TableName);

        var options = SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepNulls;
        if (generator.KeepIdentity)
            options |= SqlBulkCopyOptions.KeepIdentity;

        using var bulk = new SqlBulkCopy(conn, options, externalTransaction: null)
        {
            DestinationTableName = generator.TableName,
            BatchSize = generator.BatchSize,
            BulkCopyTimeout = 0,
        };

        var buffer = new DataTable();
        foreach (var planned in generator.Columns)
        {
            var clr = SqlTypeMapper.ClrType(planned.Column);
            buffer.Columns.Add(planned.Column.Name, clr);
            bulk.ColumnMappings.Add(planned.Column.Name, planned.Column.Name);
        }

        long total = 0;
        int batchSize = generator.BatchSize;
        foreach (var row in generator.Rows())
        {
            var dataRow = buffer.NewRow();
            for (int i = 0; i < row.Length; i++)
                dataRow[i] = row[i] ?? DBNull.Value;
            buffer.Rows.Add(dataRow);

            if (buffer.Rows.Count >= batchSize)
            {
                bulk.WriteToServer(buffer);
                total += buffer.Rows.Count;
                buffer.Clear();
                OnBatchLoaded?.Invoke(total);
            }
        }

        if (buffer.Rows.Count > 0)
        {
            bulk.WriteToServer(buffer);
            total += buffer.Rows.Count;
            OnBatchLoaded?.Invoke(total);
        }

        return new LoadResult { RowsLoaded = total, Elapsed = start.Elapsed };
    }

    /// <summary>TRUNCATE when possible, DELETE as fallback (FK references block TRUNCATE).</summary>
    private static void ClearTable(SqlConnection conn, string tableName)
    {
        try
        {
            conn.Execute($"TRUNCATE TABLE {tableName}", commandTimeout: 0);
        }
        catch (SqlException)
        {
            conn.Execute($"DELETE FROM {tableName}", commandTimeout: 0);
        }
    }

}
