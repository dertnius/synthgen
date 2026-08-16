using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// The options every patch verb shares. Path defaults and env-var fallbacks match the
/// standalone pfandwerk CLI this branch replaced.
/// </summary>
public class PatchSettings : CommandSettings
{
    public const string TargetEnvVar = "PFANDWERK_TARGET_CONNECTION";
    public const string LedgerEnvVar = "PFANDWERK_LEDGER_CONNECTION";

    [CommandOption("--rules <PATH>")]
    [Description("Gap rules YAML.")]
    public string Rules { get; init; } = "rules/gaps.yaml";

    [CommandOption("--allowlist <PATH>")]
    [Description("Committed connection allowlist (hard rule 5).")]
    public string Allowlist { get; init; } = "allowlist.json";

    [CommandOption("--artifacts <DIR>")]
    [Description("Directory the run artifacts are written to and read from.")]
    public string Artifacts { get; init; } = "artifacts";

    [CommandOption("--consumer-checks <PATH>")]
    [Description("Consumer checks YAML; skipped when the file does not exist.")]
    public string ConsumerChecks { get; init; } = "rules/consumer-checks.yaml";

    [CommandOption("--provider <NAME>")]
    [Description("Database provider: sqlserver (default) or sqlite.")]
    public string Provider { get; init; } = "sqlserver";

    [CommandOption("--target <CONNSTR>")]
    [Description($"Target connection; falls back to {TargetEnvVar}.")]
    public string? Target { get; init; }

    [CommandOption("--ledger <CONNSTR>")]
    [Description($"Ledger connection; falls back to {LedgerEnvVar}, then the target.")]
    public string? Ledger { get; init; }

    [CommandOption("--seed <INT>")]
    [Description("Deterministic Faker seed for planned values.")]
    public int? Seed { get; init; }

    [CommandOption("--only <IDS>")]
    [Description("Comma-separated rule ids to run; everything else is left untouched.")]
    public string? Only { get; init; }

    [CommandOption("--tables <NAMES>")]
    [Description("survey only: comma-separated tables to profile. Distinct from --only, which names rule ids.")]
    public string? Tables { get; init; }

    [CommandOption("--tests <DIR>")]
    [Description("lint only: directory scanned for the test conventions of D-C2.")]
    public string Tests { get; init; } = "tests";

    [CommandOption("--sink <NAME>")]
    [Description("sql (default, transactional) or dab (REST, for sites mandating an API layer).")]
    public string Sink { get; init; } = "sql";

    [CommandOption("--dab-url <URL>")]
    [Description("Base URL of the running DAB instance when --sink dab.")]
    public string DabUrl { get; init; } = "http://localhost:5000";

    [CommandOption("-y|--yes")]
    [Description("Skip the interactive confirmation (approve, revert).")]
    public bool Yes { get; init; }

    public string TargetResolved => !string.IsNullOrEmpty(Target)
        ? Target : Environment.GetEnvironmentVariable(TargetEnvVar) ?? "";

    public string LedgerResolved => !string.IsNullOrEmpty(Ledger)
        ? Ledger : Environment.GetEnvironmentVariable(LedgerEnvVar) ?? TargetResolved;

    public Provider ParsedProvider => Provider == "sqlite"
        ? SynthGen.Core.Support.Provider.Sqlite : SynthGen.Core.Support.Provider.SqlServer;

    /// <summary>Run only these rule ids. Null means all.</summary>
    public List<string>? OnlyIds => Split(Only);

    /// <summary>survey only: which tables to profile. Null means all with rules or signals.</summary>
    public List<string>? TableNames => Split(Tables);

    private static List<string>? Split(string? csv) => csv is null
        ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

    public string Path(string name) => System.IO.Path.Combine(Artifacts, name);
    public DbContext Db() => new(ParsedProvider, TargetResolved, LedgerResolved);

    /// <summary>The reviewed vocabularies next to the rules file: &lt;dir&gt;/datasets/*.yaml.</summary>
    public DatasetStore Datasets() => DatasetStore.ForRulesFile(Rules);

    public void EnsureApplyUsesSameDatabase()
    {
        if (!string.Equals(TargetResolved, LedgerResolved, StringComparison.OrdinalIgnoreCase))
            throw new PatchAbortedException(
                "patch apply requires the target and ledger to use the same database; " +
                "separate databases cannot provide an atomic ledger reservation.");
    }

    /// <summary>
    /// Both apply and revert build the sink here, so a run reverts through the same path
    /// that applied it.
    /// </summary>
    public IPatchSink PatchSink(List<GapRule> rules)
    {
        if (string.Equals(Sink, "sql", StringComparison.OrdinalIgnoreCase))
            return new SqlPatchSink(Db(), Environment.UserName);

        if (!string.Equals(Sink, "dab", StringComparison.OrdinalIgnoreCase))
            throw new RulesLoadException($"--sink must be 'sql' or 'dab', not '{Sink}'.");

        var http = new HttpClient { BaseAddress = new Uri(DabUrl.TrimEnd('/') + "/") };
        var sink = new DabPatchSink(http, Db(), Environment.UserName);
        sink.EnsureReachable(rules);   // fail once, by URL, rather than on row one
        return sink;
    }

    public List<GapRule> LoadRules()
    {
        var all = GapRulesLoader.LoadFile(Rules).Rules;
        if (OnlyIds is not { } only) return all;

        var unknown = only.Where(id => all.All(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new RulesLoadException($"--only names unknown rule id(s): {string.Join(", ", unknown)}");
        return all.Where(r => only.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public List<Check> ConsumerChecksOrEmpty() => CheckRunner.LoadConsumerChecks(ConsumerChecks);
}

internal static class PatchSupport
{
    public static int Fail(string message, int code = ExitCodes.ConfigError)
    {
        Console.Error.WriteLine($"error: {message}");
        return code;
    }

    /// <summary>
    /// Hard rule 5. Returns null when the connection is permitted, or the exit code to
    /// return when it is not. Both `plan` and `survey` run it — anything that opens the
    /// target database goes through here first.
    /// </summary>
    public static int? Guard(PatchSettings o)
    {
        if (o.ParsedProvider == Provider.Sqlite)
        {
            // SQLite fixtures are files, not servers; the allowlist is a SQL Server concept.
            Console.WriteLine($"GUARD ok (sqlite fixture: {o.TargetResolved})");
            return null;
        }
        var allow = ConnectionAllowlist.Load(o.Allowlist);
        if (!allow.IsAllowed(o.TargetResolved, ledger: false, out var t))
            return Fail(t, ExitCodes.AllowlistMismatch);
        if (!allow.IsAllowed(o.LedgerResolved, ledger: true, out var l))
            return Fail(l, ExitCodes.AllowlistMismatch);
        Console.WriteLine($"GUARD ok (target {t}; ledger {l})");
        return null;
    }

    public static string Fmt(Dictionary<string, string> key) =>
        string.Join(", ", key.Select(k => $"{k.Key} {k.Value}"));
}
