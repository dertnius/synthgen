using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// Facts, then audit, then fallback — hard rule 8 in one place. report.md ships only if
/// the auditor passes it; otherwise the bare-facts rendering replaces it.
/// </summary>
[Description("Extract facts.json from the run artifacts and audit (or replace) report.md.")]
public sealed class ReportCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
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
}
