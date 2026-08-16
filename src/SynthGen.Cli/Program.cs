using System.Data.Common;
using Pfandwerk;
using Spectre.Console.Cli;
using SynthGen.Cli.Commands;
using SynthGen.Cli.Commands.Patch;
using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("synthgen");
    config.PropagateExceptions();

    config.AddCommand<InitCommand>("init")
        .WithExample("init", "--ddl", "customers.sql")
        .WithExample("init", "--ddl", "schema.sql", "--table", "dbo.Orders", "--out", "orders.rules.yaml");

    config.AddCommand<GenerateCommand>("generate")
        .WithExample("generate", "--ddl", "customers.sql", "--rules", "customers.rules.yaml", "--dry-run")
        .WithExample("generate", "--ddl", "customers.sql", "--rules", "customers.rules.yaml",
            "--connection", "\"Server=.;Database=Test;Integrated Security=true;TrustServerCertificate=true\"");

    config.AddCommand<EvaluateCommand>("evaluate")
        .WithExample("evaluate", "--rules", "customers.rules.yaml", "--json");

    config.AddBranch<AdventureWorksCorruptCommand.Settings>("sample", sample =>
    {
        sample.SetDescription("Deterministic sample-fixture preparation commands.");
        sample.AddCommand<AdventureWorksCorruptCommand>("adventureworks-corrupt");
    });

    // One verb per phase. A single parsed invocation is what lets the agent permission
    // layer allow exactly `synthgen patch <verb>` and nothing else.
    config.AddBranch<PatchSettings>("patch", patch =>
    {
        patch.SetDescription("Repair bad values in existing rows: survey, plan, approve, apply, verify, report.");
        patch.AddCommand<SurveyCommand>("survey");
        patch.AddCommand<LintCommand>("lint");
        patch.AddCommand<PlanCommand>("plan");
        patch.AddCommand<ApproveCommand>("approve");
        patch.AddCommand<ApplyCommand>("apply");
        patch.AddCommand<VerifyCommand>("verify");
        patch.AddCommand<ReportCommand>("report");
        patch.AddCommand<NotaryCommand>("notary");
        patch.AddCommand<RevertCommand>("revert");
        patch.AddCommand<GeneratorsCommand>("generators");
    });
});

try
{
    return app.Run(args);
}
catch (Exception ex) when (ex is DdlParseException or SynthGen.Core.Rules.RulesLoadException
                               or GenerationException or PatchAbortedException or GeneratorException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCodes.ConfigError;
}
catch (LedgerConflictException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCodes.LedgerConflict;
}
catch (DbException ex)
{
    Console.Error.WriteLine($"database error: {ex.Message}");
    return ExitCodes.RuntimeError;
}
catch (CommandParseException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCodes.ConfigError;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"unexpected error: {ex.Message}");
    return ExitCodes.RuntimeError;
}
