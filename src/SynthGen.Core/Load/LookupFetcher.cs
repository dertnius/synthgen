using System.Data;
using System.Data.Common;
using Dapper;
using SynthGen.Core.Generation;

namespace SynthGen.Core.Load;

/// <summary>
/// Fetches candidate values for "query" strategy columns via Dapper. Works against any
/// ADO.NET provider, so tests can point it at SQLite while production uses SQL Server.
/// </summary>
public static class LookupFetcher
{
    public static Dictionary<string, IReadOnlyList<object>> Fetch(
        GenerationPlan plan, Func<IDbConnection> connectionFactory)
    {
        var result = new Dictionary<string, IReadOnlyList<object>>(StringComparer.OrdinalIgnoreCase);
        if (plan.Lookups.Count == 0) return result;

        using var conn = connectionFactory();
        conn.Open();
        foreach (var (column, sql) in plan.Lookups)
        {
            try
            {
                // First column of each row. (Query<object> would yield DapperRow
                // objects, whose ToString() silently poisons the generated values.)
                var values = new List<object>();
                using var reader = conn.ExecuteReader(sql);
                while (reader.Read())
                {
                    var value = reader.GetValue(0);
                    if (value is not DBNull) values.Add(value);
                }
                result[column] = values;
            }
            catch (DbException ex)
            {
                throw new GenerationException(
                    $"Column '{column}': lookup query failed: {ex.Message} (SQL: {sql})");
            }
        }
        return result;
    }
}
