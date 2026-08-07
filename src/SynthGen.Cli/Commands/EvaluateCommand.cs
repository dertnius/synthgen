using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using SynthGen.Cli;
using SynthGen.Core.Eval;
using SynthGen.Core.Rules;

namespace SynthGen.Cli.Commands;

[Description("Run only the evaluations from a rules YAML against the database.")]
public sealed class EvaluateCommand : Command<EvaluateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-u|--rules <PATH>")]
        [Description("Path to the rules YAML containing the evaluations.")]
        public string RulesPath { get; init; } = "";

        [CommandOption("-c|--connection <CONNSTR>")]
        [Description("SQL Server connection string; falls back to SYNTHGEN_CONNECTION.")]
        public string? Connection { get; init; }

        [CommandOption("--json")]
        [Description("Emit a machine-readable JSON report on stdout.")]
        public bool Json { get; init; }

        [CommandOption("-p|--provider <NAME>")]
        [Description("Database provider: sqlserver (default) or sqlite.")]
        public string Provider { get; init; } = "sqlserver";
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.RulesPath))
        {
            Console.Error.WriteLine("error: --rules is required.");
            return ExitCodes.ConfigError;
        }

        var rules = RulesLoader.LoadFile(settings.RulesPath);
        if (rules.Evaluations.Count == 0)
        {
            Console.Error.WriteLine("error: the rules file defines no evaluations.");
            return ExitCodes.ConfigError;
        }

        var connection = CliSupport.ResolveConnection(settings.Connection);
        if (connection is null)
        {
            Console.Error.WriteLine(
                $"error: no connection string. Pass --connection or set {CliSupport.ConnectionEnvVar}.");
            return ExitCodes.ConfigError;
        }

        Evaluator evaluator;
        if (settings.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (!SynthGen.Sqlite.SqliteNative.TryInitialize(out var sqliteError))
            {
                Console.Error.WriteLine($"error: {sqliteError}");
                return ExitCodes.ConfigError;
            }
            // Attach the schema of the rules' target table (default dbo) so MSSQL-style
            // [schema].[table] queries resolve.
            var schema = rules.Table?.Contains('.') == true
                ? rules.Table.Split('.')[0].Trim('[', ']')
                : "dbo";
            var factory = new SynthGen.Sqlite.SqliteConnectionFactory(
                CliSupport.ExtractSqliteDataSource(connection), new[] { schema });
            evaluator = new Evaluator(() => factory.Open());
        }
        else if (settings.Provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            evaluator = new Evaluator(connection);
        }
        else
        {
            Console.Error.WriteLine($"error: unknown provider '{settings.Provider}'. Use sqlserver or sqlite.");
            return ExitCodes.ConfigError;
        }

        var results = evaluator.Run(rules.Evaluations);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Evaluations = results,
                Passed = CliSupport.AllPassed(results),
            }, CliSupport.JsonOptions));
        }
        else
        {
            Console.WriteLine($"Evaluations ({settings.RulesPath}):");
            CliSupport.PrintEvaluations(results, Console.Out);
        }

        return CliSupport.AllPassed(results) ? ExitCodes.Ok : ExitCodes.EvaluationFailed;
    }
}
