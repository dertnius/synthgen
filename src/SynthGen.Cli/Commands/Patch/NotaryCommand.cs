using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>The CI notary. No database, no Copilot — committed artifacts only.</summary>
[Description("Audit the committed run artifacts in CI and write notary.json.")]
public sealed class NotaryCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        var audit = ArtifactAuditor.Audit(o.Artifacts, o.Rules, Directory.GetCurrentDirectory());
        Json.Write(o.Path("notary.json"), audit);
        Console.WriteLine($"NOTARY {audit.Verdict}");
        foreach (var v in audit.Violations) Console.WriteLine($"  [{v.Kind}] {v.Detail}");
        return audit.Verdict == "pass" ? ExitCodes.Ok : ExitCodes.ConfigError;
    }
}
