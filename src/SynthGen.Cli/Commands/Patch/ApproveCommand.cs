using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// Tier 1 lists every new identity in full because they are permanent; tier 2 shows the
/// frozen values and, for derived rules, the inputs beside the outputs — which is the
/// only way a derived value is reviewable at all.
/// </summary>
[Description("Review the plan at the two-tier gate and bind plan.approved to its SHA-256.")]
public sealed class ApproveCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
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
                foreach (var i in r.NewIdentities) Console.WriteLine($"    {PatchSupport.Fmt(i.Key)} -> {i.Value}");
            }
            else if (r.Patches.Count > 0)
            {
                Console.WriteLine("  TIER 2 — values to be written:");
                foreach (var p in r.Patches.Take(5))
                {
                    var inputs = p.Inputs is null ? ""
                        : " (" + string.Join(", ", p.Inputs.Select(kv => $"{kv.Key} {kv.Value ?? "NULL"}")) + ")";
                    Console.WriteLine($"    {PatchSupport.Fmt(p.Key)}{inputs} -> {p.Value}");
                }
                if (r.Patches.Count > 5) Console.WriteLine($"    … and {r.Patches.Count - 5} more");
            }
            foreach (var s in r.Skipped) Console.WriteLine($"  SKIPPED {PatchSupport.Fmt(s.Key)}: {s.Why}  [{s.Policy}]");
            PrintCoverage(r.Coverage);
            Console.WriteLine();
        }

        var blocked = plan.Rules.Count(r => r.Status == "BLOCKED");
        if (blocked > 0)
            return PatchSupport.Fail($"{blocked} rule(s) BLOCKED past threshold; refusing to approve.");

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

    /// <summary>
    /// The only check that asks whether a rule selects the rows a person meant — everything
    /// else validates its shape. Advisory: it never blocks, because a row whose inputs are
    /// unusable is a legitimate uncovered violation and refusing that run would be wrong.
    /// </summary>
    private static void PrintCoverage(RuleCoverage? c)
    {
        if (c is null) return;
        if (c.UncoveredViolations == 0 && c.SelectedButValid == 0 && c.Indeterminate == 0) return;

        // Phrased as participles rather than finite verbs so one wording reads correctly for
        // a single row and for many.
        Console.WriteLine("  COVERAGE");
        if (c.UncoveredViolations > 0)
            Line(c.UncoveredViolations, c.UncoveredKeys,
                 "breaking the invariant but not selected by the gap — predicate may be too narrow");
        if (c.SelectedButValid > 0)
            Line(c.SelectedButValid, c.SelectedButValidKeys,
                 "selected by the gap but already satisfying the invariant — predicate may be too broad");
        if (c.Indeterminate > 0)
            Line(c.Indeterminate, c.IndeterminateKeys,
                 "the invariant cannot evaluate — a NULL in a column it references, silently passed by verify");

        static void Line(int count, List<string> keys, string what)
        {
            Console.WriteLine($"    {count} row{(count == 1 ? "" : "s")} {what}");
            if (keys.Count > 0)
                Console.WriteLine($"      {string.Join(", ", keys)}{(count > keys.Count ? $", … {count - keys.Count} more" : "")}");
        }
    }
}
