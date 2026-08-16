using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

[Description("Apply the approved plan through the configured sink and log patches.jsonl.")]
public sealed class ApplyCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        o.EnsureApplyUsesSameDatabase();
        var rules = o.LoadRules();
        var applied = new Patcher(o.PatchSink(rules), rules)
            .Apply(o.Path("plan.json"), o.Path("plan.approved"), o.Path("patches.jsonl"));
        Console.WriteLine($"APPLY {applied} rows patched via {o.Sink} -> {o.Path("patches.jsonl")}");
        return ExitCodes.Ok;
    }
}
