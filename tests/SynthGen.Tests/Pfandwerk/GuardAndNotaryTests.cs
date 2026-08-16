using System.Diagnostics;
using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>
/// Hard rule 5's enforcement. The allowlist is what stands between a mistyped connection
/// string and a write to the wrong database, so its matching must be canonical and its
/// credential rejection absolute.
/// </summary>
public class ConnectionAllowlistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-allow").FullName;

    private ConnectionAllowlist Load(string json)
    {
        var path = Path.Combine(_dir, "allowlist.json");
        File.WriteAllText(path, json);
        return ConnectionAllowlist.Load(path);
    }

    private const string Committed = """
        {
          "targets": [ { "server": "localhost\\SQLEXPRESS", "database": "PropertyDev", "auth": "integrated" } ],
          "ledger":  [ { "server": "localhost\\SQLEXPRESS", "database": "PfandwerkConfig", "auth": "integrated" } ]
        }
        """;

    [Fact]
    public void A_case_and_whitespace_variant_matches()
    {
        var allow = Load(Committed);
        Assert.True(allow.IsAllowed(
            "Server= LOCALHOST\\sqlexpress ;Database= propertydev ;Integrated Security=true",
            ledger: false, out _));
    }

    [Fact]
    public void A_database_mismatch_is_rejected_with_detail()
    {
        var allow = Load(Committed);
        Assert.False(allow.IsAllowed(
            "Server=localhost\\SQLEXPRESS;Database=PropertyProd;Integrated Security=true",
            ledger: false, out var detail));
        Assert.Contains("PropertyProd", detail);
        Assert.Contains("targets", detail);
    }

    [Fact]
    public void An_auth_mode_mismatch_is_rejected()
    {
        // Same server and database over SQL auth is a different trust decision.
        var allow = Load(Committed);
        Assert.False(allow.IsAllowed(
            "Server=localhost\\SQLEXPRESS;Database=PropertyDev;User Id=svc;Password=x",
            ledger: false, out _));
    }

    [Fact]
    public void The_ledger_section_does_not_authorize_targets()
    {
        var allow = Load(Committed);
        Assert.False(allow.IsAllowed(
            "Server=localhost\\SQLEXPRESS;Database=PfandwerkConfig;Integrated Security=true",
            ledger: false, out _));
        Assert.True(allow.IsAllowed(
            "Server=localhost\\SQLEXPRESS;Database=PfandwerkConfig;Integrated Security=true",
            ledger: true, out _));
    }

    [Fact]
    public void A_credential_bearing_entry_is_rejected_at_load()
    {
        // The file is committed; a pasted credential would enter git history permanently.
        var ex = Assert.Throws<InvalidOperationException>(() => Load("""
            { "targets": [ { "server": "localhost;Password=hunter2", "database": "Dev", "auth": "sql" } ] }
            """));
        Assert.Contains("credential", ex.Message);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

/// <summary>
/// The notary (D13): a consistent artifact set passes, and each kind of drift it exists to
/// catch turns the verdict red. Runs in CI after the E2E, so this is live enforcement.
/// </summary>
public class ArtifactAuditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-notary").FullName;
    private string Artifacts => Path.Combine(_dir, "artifacts");
    private string RulesPath => Path.Combine(_dir, "gaps.yaml");

    /// <summary>One rule, one patch, one identity — internally consistent by construction.</summary>
    private void WriteConsistentRun(string approverEmail = "reviewer@example.test")
    {
        Directory.CreateDirectory(Artifacts);
        File.WriteAllText(RulesPath, "rules: []   # content only matters for its hash\n");

        var key = new Dictionary<string, string> { ["PropertyId"] = "102" };
        var plan = new PlanDocument("20260816T000000Z-test", Json.Sha256File(RulesPath),
            new List<RulePlan>
            {
                new("SEC-T1", "dbo.Security", "SecurityId", "identity", "OK", 1, 50, "test",
                    new List<PlannedPatch> { new(key, "DE000TEST01", null) },
                    new List<SkippedRow>(),
                    new List<NewIdentity> { new(key, "DE000TEST01") }),
            });
        Json.Write(P("plan.json"), plan);
        Json.Write(P("plan.approved"),
            new Approval(Json.Sha256File(P("plan.json")), "tester", approverEmail, "2026-08-16T00:00:00Z"));
        File.WriteAllText(P("patches.jsonl"),
            """{"ts":"2026-08-16T00:00:01Z","rule":"SEC-T1","id":{"PropertyId":"102"},"col":"SecurityId","old":null,"new":"DE000TEST01","reason":"test"}""" + "\n");
        Json.Write(P("verify.json"), new VerifyDocument(plan.RunId, true, true, true,
            new List<string>(), new List<string>(), new List<CheckResult>(),
            new List<CheckResult>(), new List<string>()));
        Json.Write(P("facts.json"), FactExtractor.Extract(
            P("patches.jsonl"), P("verify.json"), P("plan.json"), P("plan.approved")));
    }

    private string P(string name) => Path.Combine(Artifacts, name);

    [Fact]
    public void A_consistent_run_passes()
    {
        WriteConsistentRun();
        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath);
        Assert.Equal("pass", audit.Verdict);
    }

    [Fact]
    public void A_plan_edited_after_approval_fails()
    {
        WriteConsistentRun();
        File.AppendAllText(P("plan.json"), " ");
        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath);
        Assert.Contains(audit.Violations, v => v.Kind == "plan-hash");
    }

    [Fact]
    public void Rules_changed_since_the_plan_fail()
    {
        WriteConsistentRun();
        File.AppendAllText(RulesPath, "# edited after the run\n");
        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath);
        Assert.Contains(audit.Violations, v => v.Kind == "rules-drift");
    }

    [Fact]
    public void A_mutated_patch_count_in_facts_fails()
    {
        WriteConsistentRun();
        File.WriteAllText(P("facts.json"),
            File.ReadAllText(P("facts.json")).Replace("\"patched\": 1", "\"patched\": 7"));
        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath);
        Assert.Contains(audit.Violations, v => v.Kind == "facts-drift");
    }

    [Fact]
    public void A_missing_artifact_fails_by_name()
    {
        WriteConsistentRun();
        File.Delete(P("patches.jsonl"));
        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath);
        Assert.Equal("fail", audit.Verdict);
        Assert.Contains(audit.Violations,
            v => v.Kind == "missing-artifact" && v.Detail.Contains("patches.jsonl"));
    }

    [Fact]
    public void An_approver_who_last_changed_the_rules_fails_four_eyes()
    {
        WriteConsistentRun(approverEmail: "author@example.test");
        Git("init", "-q", "-b", "main");
        Git("add", "gaps.yaml");
        Git("-c", "user.email=author@example.test", "-c", "user.name=Author",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "rules");

        var audit = ArtifactAuditor.Audit(Artifacts, RulesPath, repoRoot: _dir);
        Assert.Contains(audit.Violations, v => v.Kind == "four-eyes");

        // A different approver on the same history is fine (D11 is about the pairing).
        WriteConsistentRun(approverEmail: "reviewer@example.test");
        Assert.Equal("pass", ArtifactAuditor.Audit(Artifacts, RulesPath, repoRoot: _dir).Verdict);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
        Assert.Equal(0, p.ExitCode);
    }

    public void Dispose()
    {
        // Git object files are read-only; a plain recursive delete throws on Windows.
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
