using SynthGen.Core.Eval;

namespace SynthGen.Cli;

public static class CliSupport
{
    public const string ConnectionEnvVar = "SYNTHGEN_CONNECTION";

    /// <summary>--connection option first, SYNTHGEN_CONNECTION env var second.</summary>
    public static string? ResolveConnection(string? option) =>
        !string.IsNullOrWhiteSpace(option)
            ? option
            : Environment.GetEnvironmentVariable(ConnectionEnvVar);

    /// <summary>
    /// For --provider sqlite the connection may be a plain file path or a
    /// "Data Source=path" connection string; both resolve to the database file.
    /// </summary>
    public static string ExtractSqliteDataSource(string connection)
    {
        if (!connection.Contains('=')) return connection;
        foreach (var part in connection.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 &&
                kv[0].Trim().Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return connection;
    }

    public static void PrintEvaluations(IReadOnlyList<EvaluationResult> results, TextWriter writer)
    {
        foreach (var r in results)
        {
            string status = r.Informational ? "INFO" : r.Passed ? "PASS" : "FAIL";
            string detail = r.Informational
                ? $"value = {FormatValue(r.Value)}"
                : $"value = {FormatValue(r.Value)}, expected {r.Expected}";
            if (r.Error is not null) detail += $" ({r.Error})";
            writer.WriteLine($"  [{status}] {r.Name}: {detail}");
        }

        int failed = results.Count(r => !r.Passed);
        int checks = results.Count(r => !r.Informational);
        writer.WriteLine($"  {checks - failed}/{checks} checks passed" +
                         (results.Count > checks ? $", {results.Count - checks} informational" : ""));
    }

    public static bool AllPassed(IReadOnlyList<EvaluationResult> results) => results.All(r => r.Passed);

    private static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
    };
}
