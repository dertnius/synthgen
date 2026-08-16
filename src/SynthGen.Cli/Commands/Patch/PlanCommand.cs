using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// Guard, scan and plan. One pass: the rows a rule matches are only ever needed in
/// order to decide their values, so no artifact sits between finding and planning.
/// </summary>
[Description("Guard the connection, scan for gaps, and freeze every value into plan.json.")]
public sealed class PlanCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        var rules = o.LoadRules();
        var db = o.Db();

        if (PatchSupport.Guard(o) is { } refused) return refused;

        var ledger = new LedgerRepository(db);
        ledger.EnsureCreated();

        var runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..4];
        var plan = new Planner(db, ledger, rules, o.Seed, o.Datasets()).Plan(runId, Json.Sha256File(o.Rules));
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
}
