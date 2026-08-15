using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Pfandwerk;

// One verb per phase, on one CLI. A single parsed invocation is what lets the agent
// permission layer allow exactly `pfandwerk <verb>` and nothing else.

public static class Program
{
    private const string Usage =
        "usage: pfandwerk <survey|plan|approve|apply|verify|report|notary|revert|generators> [options]";

    public static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o is null) return ExitCodes.ConfigError;

        try
        {
            return o.Verb switch
            {
                "survey" => Survey(o),
                "plan" => Plan(o),
                "approve" => Approve(o),
                "apply" => Apply(o),
                "verify" => Verify(o),
                "report" => Report(o),
                "notary" => Notary(o),
                "revert" => Revert(o),
                "generators" => Generators(),
                _ => Fail($"unknown verb '{o.Verb}'.\n{Usage}"),
            };
        }
        catch (GapRulesLoadException ex) { return Fail(ex.Message); }
        catch (PatchAbortedException ex) { return Fail(ex.Message); }
        catch (GeneratorException ex) { return Fail(ex.Message); }
        catch (LedgerConflictException ex) { return Fail(ex.Message, ExitCodes.LedgerConflict); }
        catch (Exception ex) { return Fail(ex.Message, ExitCodes.RuntimeError); }
    }

    private static int Fail(string message, int code = ExitCodes.ConfigError)
    {
        Console.Error.WriteLine($"error: {message}");
        return code;
    }

    // ------------------------------------------------------------------ PLAN

    /// <summary>
    /// Guard, scan and plan. One pass: the rows a rule matches are only ever needed in
    /// order to decide their values, so no artifact sits between finding and planning.
    /// </summary>
    private static int Plan(Options o)
    {
        var rules = o.LoadRules();
        var db = o.Db();

        if (Guard(o) is { } refused) return refused;

        var ledger = new LedgerRepository(db);
        ledger.EnsureCreated();

        var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..4];
        var plan = new Planner(db, ledger, rules, o.Seed).Plan(runId, Json.Sha256File(o.Rules));
        Json.Write(o.Path("plan.json"), plan);

        // Captured now, before anything changes: this is what makes "regression" and
        // "already broken" distinguishable at VERIFY.
        var baseline = new BaselineDocument(runId,
            CheckRunner.Run(db, CheckRunner.Invariants(rules)),
            CheckRunner.Run(db, o.ConsumerChecksOrEmpty()));
        Json.Write(o.Path("baseline.json"), baseline);

        foreach (var r in plan.Rules)
            Console.WriteLine($"PLAN {r.Id,-9} {r.Status,-7} {r.Count,3} gaps, {r.Patches.Count,3} to patch, " +
                              $"{r.Skipped.Count} skipped, {r.NewIdentities.Count} new identities");
        Console.WriteLine($"PLAN baseline: {baseline.Invariants.Count(i => !i.Passed)} invariant, " +
                          $"{baseline.Consumer.Count(c => !c.Passed)} consumer already failing");
        Console.WriteLine($"PLAN sha256 {Json.Sha256File(o.Path("plan.json"))}");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Hard rule 5. Returns null when the connection is permitted, or the exit code to
    /// return when it is not. Both `plan` and `survey` run it — anything that opens the
    /// target database goes through here first.
    /// </summary>
    private static int? Guard(Options o)
    {
        if (o.Provider == Provider.Sqlite)
        {
            // SQLite fixtures are files, not servers; the allowlist is a SQL Server concept.
            Console.WriteLine($"GUARD ok (sqlite fixture: {o.Target})");
            return null;
        }
        var allow = ConnectionAllowlist.Load(o.Allowlist);
        if (!allow.IsAllowed(o.Target, ledger: false, out var t))
            return Fail(t, ExitCodes.AllowlistMismatch);
        if (!allow.IsAllowed(o.Ledger, ledger: true, out var l))
            return Fail(l, ExitCodes.AllowlistMismatch);
        Console.WriteLine($"GUARD ok (target {t}; ledger {l})");
        return null;
    }

    // ---------------------------------------------------------------- SURVEY

    /// <summary>
    /// Read-only profile of the target, for drafting rules against tables nobody has
    /// written rules for yet. It writes no rule and changes no data — it reports what the
    /// columns look like and lets a person, helped by an agent, decide what that means.
    /// </summary>
    private static int Survey(Options o)
    {
        if (Guard(o) is { } refused) return refused;

        // Every rule, not the --only subset: coverage must reflect the whole rule file or
        // the survey would invite a draft for a column that already has a rule.
        var allRules = GapRulesLoader.LoadFile(o.Rules).Rules;
        var survey = new Surveyor(o.Db(), allRules).Survey(o.Tables);
        Json.Write(o.Path("survey.json"), survey);

        foreach (var t in survey.Tables)
        {
            var flagged = t.Columns.Where(c => c.CoveredByRule is null && c.Signals.Count > 0).ToList();
            Console.WriteLine($"SURVEY {t.Table,-24} {t.Rows,7} rows, {t.Columns.Count} columns, " +
                              $"{flagged.Count} unruled column(s) with signals");
            foreach (var c in flagged)
                Console.WriteLine($"         {c.Name,-18} {string.Join("; ", c.Signals)}");
        }
        Console.WriteLine($"SURVEY -> {o.Path("survey.json")}   " +
                          "(observations only; no rule is written and no data changed)");
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ GATE

    /// <summary>
    /// Tier 1 lists every new identity in full because they are permanent; tier 2 shows the
    /// frozen values and, for derived rules, the inputs beside the outputs — which is the
    /// only way a derived value is reviewable at all.
    /// </summary>
    private static int Approve(Options o)
    {
        var plan = Json.Read<PlanDocument>(o.Path("plan.json"));

        Console.WriteLine($"\n=== GATE — run {plan.RunId} ===\n");
        foreach (var r in plan.Rules)
        {
            Console.WriteLine($"{r.Id}  {r.Kind}  {r.Table}.{r.Column}   [{r.Status}]");
            Console.WriteLine($"  {r.Count} gaps, threshold {r.Threshold}");

            if (r.NewIdentities.Count > 0)
            {
                Console.WriteLine("  TIER 1 — new identities (PERMANENT, reused by every future run):");
                foreach (var i in r.NewIdentities) Console.WriteLine($"    {Fmt(i.Key)} -> {i.Value}");
            }
            else if (r.Patches.Count > 0)
            {
                Console.WriteLine("  TIER 2 — values to be written:");
                foreach (var p in r.Patches.Take(5))
                {
                    var inputs = p.Inputs is null ? ""
                        : " (" + string.Join(", ", p.Inputs.Select(kv => $"{kv.Key} {kv.Value ?? "NULL"}")) + ")";
                    Console.WriteLine($"    {Fmt(p.Key)}{inputs} -> {p.Value}");
                }
                if (r.Patches.Count > 5) Console.WriteLine($"    … and {r.Patches.Count - 5} more");
            }
            foreach (var s in r.Skipped) Console.WriteLine($"  SKIPPED {Fmt(s.Key)}: {s.Why}  [{s.Policy}]");
            Console.WriteLine();
        }

        var blocked = plan.Rules.Count(r => r.Status == "BLOCKED");
        if (blocked > 0)
            return Fail($"{blocked} rule(s) BLOCKED past threshold; refusing to approve.");

        if (!o.Yes)
        {
            Console.Write("Approve? [yes/no] ");
            if (Console.ReadLine()?.Trim() != "yes") { Console.WriteLine("not approved."); return ExitCodes.ConfigError; }
        }

        var approval = new Approval(Json.Sha256File(o.Path("plan.json")), Environment.UserName,
            Environment.GetEnvironmentVariable("PFANDWERK_GIT_EMAIL") ?? Environment.UserName,
            DateTime.UtcNow.ToString("O"));
        Json.Write(o.Path("plan.approved"), approval);
        Console.WriteLine($"APPROVED sha256 {approval.Sha256} by {approval.GitEmail}");
        return ExitCodes.Ok;
    }

    // ----------------------------------------------------------- APPLY, VERIFY

    private static int Apply(Options o)
    {
        var rules = o.LoadRules();
        var applied = new Patcher(o.PatchSink(rules), rules)
            .Apply(o.Path("plan.json"), o.Path("plan.approved"), o.Path("patches.jsonl"));
        Console.WriteLine($"APPLY {applied} rows patched via {o.Sink} -> {o.Path("patches.jsonl")}");
        return ExitCodes.Ok;
    }

    private static int Verify(Options o)
    {
        var result = new Verifier(o.Db(), o.LoadRules()).Verify(
            Json.Read<PlanDocument>(o.Path("plan.json")),
            Json.Read<BaselineDocument>(o.Path("baseline.json")),
            o.ConsumerChecksOrEmpty());
        Json.Write(o.Path("verify.json"), result);

        Console.WriteLine($"VERIFY L1 {P(result.L1)}  L2 {P(result.L2)}  L3 {P(result.L3)}");
        Console.WriteLine($"  regressions:            {List(result.Regressions)}");
        Console.WriteLine($"  pre-existing failures:  {List(result.PreexistingReds)}");
        return Verifier.ExitCodeFor(result);

        static string P(bool ok) => ok ? "pass" : "FAIL";
        static string List(List<string> xs) => xs.Count == 0 ? "none" : string.Join(", ", xs);
    }

    // ---------------------------------------------------------------- REPORT

    /// <summary>
    /// Facts, then audit, then fallback — hard rule 8 in one place. report.md ships only if
    /// the auditor passes it; otherwise the bare-facts rendering replaces it.
    /// </summary>
    private static int Report(Options o)
    {
        var facts = FactExtractor.Extract(o.Path("patches.jsonl"), o.Path("verify.json"),
                                          o.Path("plan.json"), o.Path("plan.approved"));
        Json.Write(o.Path("facts.json"), facts);
        Console.WriteLine($"FACTS {facts.Rules.Sum(r => r.Patched)} patches, {facts.NewIdentities.Count} identities");

        var reportPath = o.Path("report.md");
        if (File.Exists(reportPath))
        {
            var audit = ReportAuditor.Audit(reportPath, o.Path("facts.json"));
            Json.Write(o.Path("report.audit.json"), audit);
            Console.WriteLine($"AUDIT {audit.Verdict}");
            foreach (var v in audit.Violations) Console.WriteLine($"  [{v.Kind}] {v.Detail}");
            if (audit.Verdict == "pass") return ExitCodes.Ok;
        }

        File.WriteAllText(reportPath, ReportAuditor.Fallback(facts));
        Json.Write(o.Path("report.audit.json"), new AuditDocument("fallback", new List<AuditViolation>()));
        Console.WriteLine($"FALLBACK bare-facts report written to {reportPath}");
        return ExitCodes.Ok;
    }

    // -------------------------------------------------------- NOTARY, REVERT

    /// <summary>The CI notary. No database, no Copilot — committed artifacts only.</summary>
    private static int Notary(Options o)
    {
        var audit = ArtifactAuditor.Audit(o.Artifacts, o.Rules, Directory.GetCurrentDirectory());
        Json.Write(o.Path("notary.json"), audit);
        Console.WriteLine($"NOTARY {audit.Verdict}");
        foreach (var v in audit.Violations) Console.WriteLine($"  [{v.Kind}] {v.Detail}");
        return audit.Verdict == "pass" ? ExitCodes.Ok : ExitCodes.ConfigError;
    }

    /// <summary>Local only, never CI. Confirms unless --yes: this rewrites approved rows.</summary>
    private static int Revert(Options o)
    {
        var rules = o.LoadRules();
        var entries = Reverter.ReadLog(o.Path("patches.jsonl"));
        var scoped = o.Only is null ? entries
            : entries.Where(e => o.Only.Contains(e.Rule, StringComparer.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"REVERT would restore {scoped.Count} value(s), newest first:");
        foreach (var e in Enumerable.Reverse(scoped).Take(10))
            Console.WriteLine($"  {e.Rule}  {Fmt(e.Id)}  {e.Column}: {e.New ?? "NULL"} -> {e.Old ?? "NULL"}");
        if (scoped.Count > 10) Console.WriteLine($"  … and {scoped.Count - 10} more");
        Console.WriteLine("  Ledger rows are NOT deleted; a later run reuses the same identities.");

        if (!o.Yes)
        {
            Console.Write("Revert? [yes/no] ");
            if (Console.ReadLine()?.Trim() != "yes") { Console.WriteLine("not reverted."); return ExitCodes.Ok; }
        }

        var reverted = new Reverter(o.PatchSink(rules), rules)
            .Revert(o.Path("patches.jsonl"), o.Only?.Count == 1 ? o.Only[0] : null);
        Console.WriteLine($"REVERT {reverted} value(s) restored; ledger untouched " +
                          $"({new LedgerRepository(o.Db()).Count()} rows).");
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Every usable `fix` key. A rule naming one that is not here fails at load; adding one
    /// is a code change through a reviewed MR, because the rule file may never carry a
    /// literal value itself.
    /// </summary>
    private static int Generators()
    {
        Console.WriteLine("Random generators — for kind: ephemeral and identity");
        foreach (var k in PatchGenerators.RandomKeys) Console.WriteLine($"  {k}");
        Console.WriteLine("\nDerived generators — for kind: derived (declare `inputs`)");
        foreach (var k in PatchGenerators.DerivedKeys) Console.WriteLine($"  {k}");
        Console.WriteLine("\nNeed one that is not listed? Add it to Generators.cs and open an MR.");
        return ExitCodes.Ok;
    }

    private static string Fmt(Dictionary<string, string> key) =>
        string.Join(", ", key.Select(k => $"{k.Key} {k.Value}"));
}

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
    /// <summary>Run only these rule ids. Everything else is left untouched.</summary>
    public List<string>? Only;
    /// <summary>survey only: which tables to profile. Distinct from --only, which names rule ids.</summary>
    public List<string>? Tables;
    /// <summary>sql (default, transactional) or dab (REST, for sites mandating an API layer).</summary>
    public string Sink = "sql";
    public string DabUrl = "http://localhost:5000";

    public string Path(string name) => System.IO.Path.Combine(Artifacts, name);
    public DbContext Db() => new(Provider, Target, Ledger);

    public static Options? Parse(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: pfandwerk <survey|plan|approve|apply|verify|report|notary|revert|generators> [options]");
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
                case "--only": o.Only = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(x => x.Trim()).ToList(); break;
                case "--tables": o.Tables = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                   .Select(x => x.Trim()).ToList(); break;
                case "--sink": o.Sink = Next(); break;
                case "--dab-url": o.DabUrl = Next(); break;
                case "--yes": o.Yes = true; break;
                default: Console.Error.WriteLine($"error: unknown option {args[i]}"); return null;
            }
        }
        o.Target = string.IsNullOrEmpty(o.Target)
            ? Environment.GetEnvironmentVariable("PFANDWERK_TARGET_CONNECTION") ?? "" : o.Target;
        o.Ledger = string.IsNullOrEmpty(o.Ledger)
            ? Environment.GetEnvironmentVariable("PFANDWERK_LEDGER_CONNECTION") ?? o.Target : o.Ledger;
        return o;
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
            throw new GapRulesLoadException($"--sink must be 'sql' or 'dab', not '{Sink}'.");

        var http = new HttpClient { BaseAddress = new Uri(DabUrl.TrimEnd('/') + "/") };
        var sink = new DabPatchSink(http, Db(), Environment.UserName);
        sink.EnsureReachable(rules);   // fail once, by URL, rather than on row one
        return sink;
    }

    public List<GapRule> LoadRules()
    {
        var all = GapRulesLoader.LoadFile(Rules).Rules;
        if (Only is null) return all;

        var unknown = Only.Where(id => all.All(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
            throw new GapRulesLoadException($"--only names unknown rule id(s): {string.Join(", ", unknown)}");
        return all.Where(r => Only.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private sealed class ConsumerChecksFile { public List<ConsumerCheck> Checks { get; set; } = new(); }
    private sealed class ConsumerCheck
    {
        public string Name { get; set; } = "";
        public string Query { get; set; } = "";
        public int Expected { get; set; }
    }

    /// <summary>
    /// The consumer suite as SQL assertions, so the baseline and the post-run comparison
    /// run through one code path.
    /// </summary>
    public List<Check> ConsumerChecksOrEmpty()
    {
        if (!File.Exists(ConsumerChecks)) return new List<Check>();
        var yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build()
            .Deserialize<ConsumerChecksFile>(File.ReadAllText(ConsumerChecks));
        return yaml.Checks.Select(c => new Check(c.Name, c.Query, c.Expected)).ToList();
    }
}
