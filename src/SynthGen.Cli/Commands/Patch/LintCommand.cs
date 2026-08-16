using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// The offline self-check of D-B3: everything a rules author can get wrong that needs no
/// database. The authoring skill runs this before opening its MR, so iteration happens
/// here instead of as broken MRs in the history the notary audits. CI covers the DB half.
/// </summary>
[Description("Validate rules, datasets, thresholds and test conventions offline; no database.")]
public sealed class LintCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        var surveyPath = File.Exists(o.Path("survey.json")) ? o.Path("survey.json") : null;
        var lint = Linter.Run(o.Rules, surveyPath, Directory.Exists(o.Tests) ? o.Tests : null);
        Json.Write(o.Path("lint.json"), lint);

        foreach (var f in lint.Findings)
            Console.WriteLine($"LINT {f.Check,-9} {f.Message}");

        Console.WriteLine(lint.Verdict == "pass"
            ? $"LINT ok ({o.Rules}, survey {(surveyPath is null ? "absent" : "consulted")}) -> {o.Path("lint.json")}"
            : $"LINT {lint.Findings.Count} finding(s) -> {o.Path("lint.json")}");
        return lint.Verdict == "pass" ? ExitCodes.Ok : ExitCodes.ConfigError;
    }
}
