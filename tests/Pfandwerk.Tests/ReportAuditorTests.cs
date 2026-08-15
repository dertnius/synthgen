using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>
/// The auditor replaces an LLM checker (D9) and runs identically in the local loop and in
/// CI, so it is the component a bad report has to get past. Tested hardest accordingly.
/// </summary>
public class ReportAuditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-audit").FullName;

    private static FactsDocument Facts() => new(
        RunId: "20260815T000000Z-test",
        PlanSha: new string('a', 64), RulesSha: new string('b', 64),
        Approver: "reviewer@example.test", Ts: "2026-08-15T00:00:00Z",
        Rules: new List<FactRule>
        {
            new("SEC-001", "Bathrooms", "derived", 4, new List<string> { "1", "2", "3" },
                "rooms drive the count", new List<SkippedRow>()),
            new("SEC-002", "SecurityId", "identity", 2,
                new List<string> { "DE000MEEKJM", "DE0002LFBHP" }, "stable reference",
                new List<SkippedRow>()),
        },
        NewIdentities: new List<NewIdentity>
        {
            new(new Dictionary<string, string> { ["PropertyId"] = "102" }, "DE000MEEKJM"),
            new(new Dictionary<string, string> { ["PropertyId"] = "104" }, "DE0002LFBHP"),
        },
        Verify: new FactsVerify(true, true, true, new List<string>(), new List<string> { "SEC-001.invariant" }));

    private AuditDocument AuditOf(string report)
    {
        var factsPath = Path.Combine(_dir, "facts.json");
        var reportPath = Path.Combine(_dir, "report.md");
        Json.Write(factsPath, Facts());
        File.WriteAllText(reportPath, report);
        return ReportAuditor.Audit(reportPath, factsPath);
    }

    private const string Honest = """
        # Run report
        ## SEC-001 — Bathrooms
        Patched 4 rows. Samples: 1, 2, 3.
        ## SEC-002 — SecurityId
        Patched 2 rows. New identities DE000MEEKJM and DE0002LFBHP are permanent.
        """;

    [Fact]
    public void A_sourced_report_passes() => Assert.Equal("pass", AuditOf(Honest).Verdict);

    [Fact]
    public void An_invented_number_fails()
    {
        var audit = AuditOf(Honest.Replace("Patched 4 rows", "Patched 7 rows"));
        Assert.Equal("fail", audit.Verdict);
        Assert.Contains(audit.Violations, v => v.Kind == "unsourced-number" && v.Detail.Contains("'7'"));
    }

    [Fact]
    public void A_dropped_rule_fails()
    {
        var audit = AuditOf("# Run report\n## SEC-001\nPatched 4 rows.");
        Assert.Equal("fail", audit.Verdict);
        Assert.Contains(audit.Violations, v => v.Kind == "missing-rule" && v.Detail.Contains("SEC-002"));
    }

    [Fact]
    public void A_dropped_identity_fails()
    {
        // Identities are permanent. A report that omits one hides a decision nobody can
        // take back, so it is called out separately from a missing rule.
        var audit = AuditOf(Honest.Replace(" and DE0002LFBHP", ""));
        Assert.Equal("fail", audit.Verdict);
        Assert.Contains(audit.Violations, v => v.Kind == "missing-identity");
    }

    [Fact]
    public void Numbers_inside_code_fences_are_not_audited()
    {
        // Fenced blocks quote artifacts verbatim; auditing them here would flag the
        // artifact's own content as unsourced.
        var audit = AuditOf(Honest + "\n```\nrandom log output 987654\n```\n");
        Assert.Equal("pass", audit.Verdict);
    }

    [Fact]
    public void The_fallback_renders_every_rule_and_identity()
    {
        var text = ReportAuditor.Fallback(Facts());
        Assert.Contains("SEC-001", text);
        Assert.Contains("SEC-002", text);
        Assert.Contains("DE000MEEKJM", text);
        Assert.Contains("narrative failed audit", text);
    }

    [Fact]
    public void The_fallback_survives_its_own_audit()
    {
        // The fallback ships when the narrative fails twice, so it must not itself be
        // rejectable — otherwise a failed run has nothing publishable at all.
        Assert.Equal("pass", AuditOf(ReportAuditor.Fallback(Facts())).Verdict);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
