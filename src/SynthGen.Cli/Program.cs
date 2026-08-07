using System.Data.Common;
using Spectre.Console.Cli;
using SynthGen.Cli;
using SynthGen.Cli.Commands;
using SynthGen.Core.Ddl;
using SynthGen.Core.Generation;
using SynthGen.Core.Rules;

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
});

try
{
    return app.Run(args);
}
catch (Exception ex) when (ex is DdlParseException or RulesLoadException or GenerationException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCodes.ConfigError;
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
