using System.Text.Json;
using Dapper;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Generation;
using Pfandwerk.Core.Phases;
using Pfandwerk.Core.Rules;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Pfandwerk.Cli;

public sealed class Options
{
    public string Verb = "";
    public string Rules = "rules/gaps.yaml";
    public string Allowlist = "allowlist.json";
    public string Artifacts = "artifacts";
    public string ConsumerChecks = "rules/consumer-checks.yaml";
    public Provider Provider = Provider.SqlServer;
    public string Target = "";
    public string Ledger = "";
    public int? Seed;
    public bool Yes;
    public bool SkipGuard;
    public string? Report;
    /// <summary>Run only these rule ids. Everything else is left untouched.</summary>
    public List<string>? Only;

    public string Path(string name) => System.IO.Path.Combine(Artifacts, name);
}

public static class Cli
{
    public static Options? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: pfandwerk <guard|scan|plan|approve|apply|verify|facts|audit> [options]");
            return null;
        }
        var o = new Options { Verb = args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{args[i - 1]} needs a value");
            switch (args[i])
            {
                case "--rules": o.Rules = Next(); break;
                case "--allowlist": o.Allowlist = Next(); break;
                case "--artifacts": o.Artifacts = Next(); break;
                case "--consumer-checks": o.ConsumerChecks = Next(); break;
                case "--provider": o.Provider = Next() == "sqlite" ? Provider.Sqlite : Provider.SqlServer; break;
                case "--target": o.Target = Next(); break;
                case "--ledger": o.Ledger = Next(); break;
                case "--seed": o.Seed = int.Parse(Next()); break;
                case "--report": o.Report = Next(); break;
                case "--only": o.Only = Next().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList(); break;
                case "--yes": o.Yes = true; break;
                case "--skip-guard": o.SkipGuard = true; break;
                default: Console.Error.WriteLine($"error: unknown option {args[i]}"); return null;
            }
        }
        o.Target = string.IsNullOrEmpty(o.Target)
            ? Environment.GetEnvironmentVariable("PFANDWERK_TARGET_CONNECTION") ?? "" : o.Target;
        o.Ledger = string.IsNullOrEmpty(o.Ledger)
            ? Environment.GetEnvironmentVariable("PFANDWERK_LEDGER_CONNECTION") ?? o.Target : o.Ledger;
        return o;
    }
}

public static class Verbs
{
    private static List<GapRule> LoadRules(Options o)
    {
        var all = GapRulesLoader.LoadFile(o.Rules).Rules;
        if (o.Only is null) return all;

        var selected = all.Where(r => o.Only.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var unknown = o.Only.Where(id => all.All(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new GapRulesLoadException($"--only names unknown rule id(s): {string.Join(", ", unknown)}");
        return selected;
    }
    private static string RulesSha(Options o) => Json.Sha256File(o.Rules);
    private static DbContext Db(Options o) => new(o.Provider, o.Target, o.Ledger);

    private static string RunId(Options o)
    {
        var path = o.Path("run-id");
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..4];
        Directory.CreateDirectory(o.Artifacts);
        File.WriteAllText(path, id);
        return id;
    }

    public static int Guard(Options o)
    {
        // SQLite fixtures are files, not servers; the allowlist is a SQL Server concept.
        if (o.Provider == Provider.Sqlite)
        {
            Console.WriteLine($"GUARD ok (sqlite fixture: {o.Target})");
            return ExitCodes.Ok;
        }
        var allow = ConnectionAllowlist.Load(o.Allowlist);
        if (!allow.IsAllowed(o.Target, ledger: false, out var t))
        {
            Console.Error.WriteLine($"error: {t}");
            return ExitCodes.AllowlistMismatch;
        }
        if (!allow.IsAllowed(o.Ledger, ledger: true, out var l))
        {
            Console.Error.WriteLine($"error: {l}");
            return ExitCodes.AllowlistMismatch;
        }
        Console.WriteLine($"GUARD ok (target {t}; ledger {l})");
        return ExitCodes.Ok;
    }

    public static int Scan(Options o)
    {
        if (!o.SkipGuard && Guard(o) != ExitCodes.Ok) return ExitCodes.AllowlistMismatch;

        var rules = LoadRules(o);
        var db = Db(o);
        new LedgerRepository(db).EnsureCreated();

        var gaps = new Scanner(db, rules).Scan(RunId(o), RulesSha(o));
        Json.Write(o.Path("gaps.json"), gaps);

        var baseline = new BaselineDocument(gaps.RunId,
            CheckRunner.Run(db, CheckRunner.InvariantChecks(rules)),
            CheckRunner.Run(db, LoadConsumerChecks(o)));
        Json.Write(o.Path("baseline.json"), baseline);

        foreach (var r in gaps.Rules)
            Console.WriteLine($"SCAN {r.Id,-9} {r.Count,3} gaps  (threshold {r.Threshold})");
        Console.WriteLine($"SCAN baseline: {baseline.Invariants.Count(i => !i.Passed)} invariant, " +
                          $"{baseline.Consumer.Count(c => !c.Passed)} consumer already failing");
        return ExitCodes.Ok;
    }

    public static int Plan(Options o)
    {
        var rules = LoadRules(o);
        var db = Db(o);
        var gaps = Json.Read<GapsDocument>(o.Path("gaps.json"));

        var plan = new Planner(db, new LedgerRepository(db), rules, o.Seed).Plan(gaps);
        Json.Write(o.Path("plan.json"), plan);
        File.WriteAllText(o.Path("plan.sha256"), Json.Sha256File(o.Path("plan.json")) + "\n");

        foreach (var r in plan.Rules)
            Console.WriteLine($"PLAN {r.Id,-9} {r.Status,-7} {r.Patches.Count,3} to patch, " +
                              $"{r.Skipped.Count} skipped, {r.NewIdentities.Count} new identities");
        Console.WriteLine($"PLAN sha256 {Json.Sha256File(o.Path("plan.json"))}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The gate. Tier 1 lists every new identity in full because they are permanent; tier 2
    /// shows counts, the frozen values, and — for derived rules — the inputs beside the
    /// outputs, which is the only way a derived value is reviewable at all.
    /// </summary>
    public static int Approve(Options o)
    {
        var plan = Json.Read<PlanDocument>(o.Path("plan.json"));
        var blocked = plan.Rules.Where(r => r.Status == "BLOCKED").ToList();

        Console.WriteLine($"\n=== GATE — run {plan.RunId} ===\n");
        foreach (var r in plan.Rules)
        {
            Console.WriteLine($"{r.Id}  {r.Kind}  {r.Table}.{r.Column}   [{r.Status}]");
            Console.WriteLine($"  {r.Count} gaps, threshold {r.Threshold}");

            if (r.NewIdentities.Count > 0)
            {
                Console.WriteLine("  TIER 1 — new identities (PERMANENT, reused by every future run):");
                foreach (var i in r.NewIdentities)
                    Console.WriteLine($"    {Fmt(i.Key)} -> {i.Value}");
            }
            if (r.Patches.Count > 0 && r.NewIdentities.Count == 0)
            {
                Console.WriteLine("  TIER 2 — values to be written:");
                foreach (var p in r.Patches.Take(5))
                {
                    var inputs = p.Inputs is null ? "" :
                        " (" + string.Join(", ", p.Inputs.Select(kv => $"{kv.Key} {kv.Value ?? "NULL"}")) + ")";
                    Console.WriteLine($"    {Fmt(p.Key)}{inputs} -> {p.Value}");
                }
                if (r.Patches.Count > 5) Console.WriteLine($"    … and {r.Patches.Count - 5} more");
            }
            foreach (var s in r.Skipped)
                Console.WriteLine($"  SKIPPED {Fmt(s.Key)}: {s.Why}  [{s.Policy}]");
            Console.WriteLine();
        }

        if (blocked.Count > 0)
        {
            Console.Error.WriteLine($"error: {blocked.Count} rule(s) BLOCKED past threshold; refusing to approve.");
            return ExitCodes.ConfigError;
        }

        if (!o.Yes)
        {
            Console.Write("Approve? [yes/no] ");
            if (Console.ReadLine()?.Trim() != "yes")
            {
                Console.WriteLine("not approved.");
                return ExitCodes.ConfigError;
            }
        }

        var approval = new Approval(Json.Sha256File(o.Path("plan.json")),
            Environment.UserName,
            Environment.GetEnvironmentVariable("PFANDWERK_GIT_EMAIL") ?? Environment.UserName,
            DateTime.UtcNow.ToString("O"));
        Json.Write(o.Path("plan.approved"), approval);
        Console.WriteLine($"APPROVED sha256 {approval.Sha256} by {approval.GitEmail}");
        return ExitCodes.Ok;
    }

    public static int Apply(Options o)
    {
        var rules = LoadRules(o);
        var db = Db(o);
        var sink = new SqlPatchSink(db, Environment.UserName);
        var applied = new Patcher(sink, rules).Apply(
            o.Path("plan.json"), o.Path("plan.approved"), o.Path("patches.jsonl"));
        Console.WriteLine($"APPLY {applied} rows patched -> {o.Path("patches.jsonl")}");
        return ExitCodes.Ok;
    }

    public static int Verify(Options o)
    {
        var rules = LoadRules(o);
        var db = Db(o);
        var plan = Json.Read<PlanDocument>(o.Path("plan.json"));
        var baseline = Json.Read<BaselineDocument>(o.Path("baseline.json"));

        var result = new Verifier(db, rules).Verify(plan, baseline, LoadConsumerChecks(o));
        Json.Write(o.Path("verify.json"), result);

        Console.WriteLine($"VERIFY L1 {(result.L1 ? "pass" : "FAIL")}  " +
                          $"L2 {(result.L2 ? "pass" : "FAIL")}  L3 {(result.L3 ? "pass" : "FAIL")}");
        Console.WriteLine($"  regressions:            {(result.Regressions.Count == 0 ? "none" : string.Join(", ", result.Regressions))}");
        Console.WriteLine($"  pre-existing failures:  {(result.PreexistingReds.Count == 0 ? "none" : string.Join(", ", result.PreexistingReds))}");
        return Verifier.ExitCodeFor(result);
    }

    public static int Facts(Options o)
    {
        var facts = FactExtractor.Extract(o.Path("patches.jsonl"), o.Path("verify.json"),
                                          o.Path("plan.json"), o.Path("plan.approved"));
        Json.Write(o.Path("facts.json"), facts);
        Console.WriteLine($"FACTS {facts.Rules.Sum(r => r.Patched)} patches, " +
                          $"{facts.NewIdentities.Count} identities -> {o.Path("facts.json")}");
        return ExitCodes.Ok;
    }

    public static int Audit(Options o)
    {
        var reportPath = o.Report ?? o.Path("report.md");
        if (!File.Exists(reportPath))
        {
            Console.Error.WriteLine($"error: no report at {reportPath}");
            return ExitCodes.ConfigError;
        }
        var audit = ReportAuditor.Audit(reportPath, o.Path("facts.json"));
        Json.Write(o.Path("report.audit.json"), audit);

        Console.WriteLine($"AUDIT {audit.Verdict}");
        foreach (var v in audit.Violations) Console.WriteLine($"  [{v.Kind}] {v.Detail}");
        return audit.Verdict == "pass" ? ExitCodes.Ok : ExitCodes.ConfigError;
    }

    /// <summary>
    /// Builds the throwaway SQLite fixture for the offline end-to-end. SQLite only — this
    /// exists so the example runs with no server, and it refuses to touch SQL Server.
    /// </summary>
    public static int Fixture(Options o)
    {
        if (o.Provider != Provider.Sqlite)
        {
            Console.Error.WriteLine("error: 'fixture' builds the local SQLite fixture only; use --provider sqlite.");
            return ExitCodes.ConfigError;
        }

        var script = File.ReadAllText("db/pfandwerk/fixtures/security.sqlite.sql");
        var db = Db(o);
        using (var conn = db.OpenTarget())
        {
            foreach (var batch in script.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                        .Where(b => !string.IsNullOrWhiteSpace(b)))
                conn.Execute(batch);
        }
        new LedgerRepository(db).EnsureCreated();

        using var check = db.OpenTarget();
        var rows = check.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Security");
        Console.WriteLine($"FIXTURE dbo.Security seeded with {rows} rows; ledger ready ({o.Target})");
        return ExitCodes.Ok;
    }

    private static string Fmt(Dictionary<string, string> key) =>
        string.Join(", ", key.Select(k => $"{k.Key} {k.Value}"));

    private sealed class ConsumerChecksFile { public List<ConsumerCheck> Checks { get; set; } = new(); }
    private sealed class ConsumerCheck
    {
        public string Name { get; set; } = "";
        public string Query { get; set; } = "";
        public int Expected { get; set; }
    }

    /// <summary>
    /// The consumer suite as SQL assertions. One runner serves the baseline and the
    /// post-run comparison, so both halves of the diff are produced identically.
    /// </summary>
    private static List<Check> LoadConsumerChecks(Options o)
    {
        if (!File.Exists(o.ConsumerChecks)) return new List<Check>();
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build()
            .Deserialize<ConsumerChecksFile>(File.ReadAllText(o.ConsumerChecks));
        return yaml.Checks.Select(c => new Check(c.Name, c.Query, c.Expected)).ToList();
    }
}
