using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;

namespace Pfandwerk;

// Step zero: what does this database look like, and which columns look suspicious?
//
// pfandwerk finds rows matching a predicate somebody already wrote. It has never had a way
// to answer "which tables have no rules yet, and what is wrong with them" — that was always
// a person reading schemas. This produces the facts an agent can draft rules from.
//
// It reports OBSERVATIONS, never verdicts. "EnergyClass is 34% NULL and 12% 'X9'" is a
// fact; "EnergyClass is broken" is a judgement, and judgement belongs to whoever writes
// the rule and approves the run.

public sealed record ValueCount(string Value, long Count);

public sealed record ColumnSurvey(
    string Name, string Type, bool Nullable, bool IsKey,
    int Nulls, double NullRate, int Distinct,
    List<ValueCount> TopValues,
    string? CoveredByRule,
    List<string> Signals);

public sealed record TableSurvey(string Table, int Rows, List<ColumnSurvey> Columns);

public sealed record SurveyDocument(string Ts, List<TableSurvey> Tables);

public sealed class Surveyor
{
    private const int TopValueCount = 5;

    private readonly DbContext _db;
    private readonly List<GapRule> _rules;

    public Surveyor(DbContext db, List<GapRule> rules) => (_db, _rules) = (db, rules);

    public SurveyDocument Survey(IEnumerable<string>? only)
    {
        using var conn = _db.OpenTarget();
        var tables = (only?.ToList() is { Count: > 0 } picked ? picked : ListTables(conn)).ToList();

        return new SurveyDocument(DateTime.UtcNow.ToString("O"),
            tables.Select(t => SurveyTable(conn, t)).ToList());
    }

    private TableSurvey SurveyTable(IDbConnection conn, string table)
    {
        var rows = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
        var columns = new List<ColumnSurvey>();

        var keys = PrimaryKeyColumns(conn, table);

        foreach (var (name, type, nullable) in ListColumns(conn, table))
        {
            // Binary columns have no useful distribution and can be enormous.
            if (type.Contains("binary", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("blob", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("image", StringComparison.OrdinalIgnoreCase)) continue;

            var nulls = rows == 0 ? 0 : conn.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM {table} WHERE {name} IS NULL");
            var distinct = rows == 0 ? 0 : conn.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM (SELECT DISTINCT {name} FROM {table} WHERE {name} IS NOT NULL) d");

            var top = rows == 0 ? new List<ValueCount>() : TopValues(conn, table, name);
            var rate = rows == 0 ? 0 : (double)nulls / rows;
            var covered = _rules.FirstOrDefault(r =>
                string.Equals(r.Table, table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Column, name, StringComparison.OrdinalIgnoreCase))?.Id;

            var isKey = keys.Contains(name, StringComparer.OrdinalIgnoreCase);
            columns.Add(new ColumnSurvey(name, type, nullable, isKey, nulls, Math.Round(rate, 4),
                distinct, top, covered, Signals(rows, nulls, rate, distinct, nullable, isKey, top)));
        }

        return new TableSurvey(table, rows, columns);
    }

    /// <summary>
    /// Neutral observations an agent can draft from and a human can overrule. Each is a
    /// measurement, phrased so it cannot be mistaken for a decision.
    /// </summary>
    private static List<string> Signals(int rows, int nulls, double rate, int distinct,
                                        bool nullable, bool isKey, List<ValueCount> top)
    {
        var signals = new List<string>();
        if (rows == 0) { signals.Add("table is empty"); return signals; }
        // A key is never a repair candidate, and SQLite reports INTEGER PRIMARY KEY as
        // nullable, which would otherwise flag every table's key column as interesting.
        if (isKey) return signals;

        if (nulls > 0 && rate >= 0.05)
            signals.Add($"{Pct(rate)} NULL");
        if (nullable && nulls == 0)
            // The interesting inverse: the schema permits NULL but the data never has one,
            // so a NULL appearing later is an anomaly worth a rule.
            signals.Add("nullable but never NULL");
        if (distinct == 1 && nulls < rows)
            signals.Add("single value across the whole table");

        // Two things are worth saying about a value distribution, and listing every value
        // is neither. A flat spread across six room counts is not a defect; one value
        // swallowing the column, or a known sentinel literal, might be.
        //
        // Deliberately NOT trying to recognise domain sentinels like 'X9' — the survey
        // cannot know that. The full distribution is in survey.json for the agent to read
        // and a person to judge.
        var nonNull = rows - nulls;
        if (nonNull > 0 && distinct > 1)
        {
            foreach (var v in top)
            {
                var share = (double)v.Count / nonNull;
                if (IsSentinelLiteral(v.Value) && share >= 0.02)
                    signals.Add($"'{v.Value}' in {Pct(share)} of non-NULL rows — common sentinel shape");
                else if (share >= 0.40)
                    signals.Add($"'{v.Value}' in {Pct(share)} of non-NULL rows — dominant");
            }
        }

        return signals;
    }

    private static bool IsSentinelLiteral(string v) =>
        v is "0" or "-1" or "" or " " or "?" or "-" ||
        v.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        v.Equals("null", StringComparison.OrdinalIgnoreCase);

    /// <summary>"34%", not the framework default "34 %" with its non-breaking space.</summary>
    private static string Pct(double d) =>
        (d * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private HashSet<string> PrimaryKeyColumns(IDbConnection conn, string table)
    {
        var (schema, name) = SplitName(table);
        if (_db.Provider == Provider.Sqlite)
        {
            return conn.Query($"PRAGMA {schema}.table_info({name})")
                .Cast<IDictionary<string, object?>>()
                .Where(r => Convert.ToInt32(r["pk"]) > 0)
                .Select(r => (string)r["name"]!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return conn.Query<string>(
            """
            SELECT ccu.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu
              ON tc.CONSTRAINT_NAME = ccu.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
              AND tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @name
            """, new { schema, name }).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (string Schema, string Name) SplitName(string table)
    {
        var parts = table.Split('.');
        return (parts.Length > 1 ? parts[0].Trim('[', ']') : "dbo", parts[^1].Trim('[', ']'));
    }

    // ---------------------------------------------------------------- provider SQL

    private IEnumerable<string> ListTables(IDbConnection conn) =>
        _db.Provider == Provider.Sqlite
            ? conn.Query<string>(
                "SELECT 'dbo.' || name FROM dbo.sqlite_master WHERE type = 'table' " +
                "AND name NOT LIKE 'sqlite_%' ORDER BY name")
            : conn.Query<string>(
                "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES " +
                "WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_SCHEMA, TABLE_NAME");

    private IEnumerable<(string Name, string Type, bool Nullable)> ListColumns(
        IDbConnection conn, string table)
    {
        var (schema, name) = SplitName(table);

        if (_db.Provider == Provider.Sqlite)
        {
            foreach (IDictionary<string, object?> r in conn.Query($"PRAGMA {schema}.table_info({name})"))
                yield return ((string)r["name"]!, (string)(r["type"] ?? "")!,
                              Convert.ToInt32(r["notnull"]) == 0);
            yield break;
        }

        foreach (var r in conn.Query(
            "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name ORDER BY ORDINAL_POSITION",
            new { schema, name }))
        {
            yield return ((string)r.COLUMN_NAME, (string)r.DATA_TYPE,
                          string.Equals((string)r.IS_NULLABLE, "YES", StringComparison.OrdinalIgnoreCase));
        }
    }

    private List<ValueCount> TopValues(IDbConnection conn, string table, string column)
    {
        var sql = _db.Provider == Provider.Sqlite
            ? $"SELECT CAST({column} AS TEXT) AS Value, COUNT(*) AS Count FROM {table} " +
              $"WHERE {column} IS NOT NULL GROUP BY {column} ORDER BY COUNT(*) DESC LIMIT {TopValueCount}"
            : $"SELECT TOP {TopValueCount} CAST({column} AS nvarchar(200)) AS Value, COUNT(*) AS Count " +
              $"FROM {table} WHERE {column} IS NOT NULL GROUP BY {column} ORDER BY COUNT(*) DESC";

        try { return conn.Query<ValueCount>(sql).ToList(); }
        catch (DbException ex)
        {
            // A type that will not cast to text should not fail the whole survey — but it
            // must not be silent either. Swallowing everything here once hid the fact that
            // this method returned nothing at all for every column.
            Console.Error.WriteLine($"warn: no value distribution for {table}.{column}: {ex.Message}");
            return new List<ValueCount>();
        }
    }
}
