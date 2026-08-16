using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>Local only, never CI. Confirms unless --yes: this rewrites approved rows.</summary>
[Description("Replay patches.jsonl newest-first to restore the previous values. Local only.")]
public sealed class RevertCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        var rules = o.LoadRules();
        var entries = Reverter.ReadLog(o.Path("patches.jsonl"));
        var scoped = o.OnlyIds is null ? entries
            : entries.Where(e => o.OnlyIds.Contains(e.Rule, StringComparer.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"REVERT would restore {scoped.Count} value(s), newest first:");
        foreach (var e in Enumerable.Reverse(scoped).Take(10))
            Console.WriteLine($"  {e.Rule}  {PatchSupport.Fmt(e.Id)}  {e.Column}: {e.New ?? "NULL"} -> {e.Old ?? "NULL"}");
        if (scoped.Count > 10) Console.WriteLine($"  … and {scoped.Count - 10} more");
        Console.WriteLine("  Ledger rows are NOT deleted; a later run reuses the same identities.");

        if (!o.Yes)
        {
            Console.Write("Revert? [yes/no] ");
            if (Console.ReadLine()?.Trim() != "yes") { Console.WriteLine("not reverted."); return ExitCodes.Ok; }
        }

        var reverted = new Reverter(o.PatchSink(rules), rules)
            .Revert(o.Path("patches.jsonl"), o.OnlyIds?.Count == 1 ? o.OnlyIds[0] : null);
        Console.WriteLine($"REVERT {reverted} value(s) restored; ledger untouched " +
                          $"({new LedgerRepository(o.Db()).Count()} rows).");
        return ExitCodes.Ok;
    }
}
