using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// Read-only profile of the target, for drafting rules against tables nobody has
/// written rules for yet. It writes no rule and changes no data — it reports what the
/// columns look like and lets a person, helped by an agent, decide what that means.
/// </summary>
[Description("Profile the target's columns read-only and write survey.json.")]
public sealed class SurveyCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        if (PatchSupport.Guard(o) is { } refused) return refused;

        // Every rule, not the --only subset: coverage must reflect the whole rule file or
        // the survey would invite a draft for a column that already has a rule.
        var allRules = GapRulesLoader.LoadFile(o.Rules).Rules;
        var survey = new Surveyor(o.Db(), allRules).Survey(o.TableNames);
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
}
