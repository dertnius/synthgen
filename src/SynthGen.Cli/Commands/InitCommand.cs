using System.ComponentModel;
using Spectre.Console.Cli;
using SynthGen.Core.Ddl;
using SynthGen.Core.Rules;

namespace SynthGen.Cli.Commands;

[Description("Scaffold a rules YAML from a CREATE TABLE DDL file.")]
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--ddl <PATH>")]
        [Description("Path to the .sql file containing the CREATE TABLE statement.")]
        public string DdlPath { get; init; } = "";

        [CommandOption("-t|--table <NAME>")]
        [Description("Table to scaffold when the script defines several ('Name' or 'Schema.Name').")]
        public string? Table { get; init; }

        [CommandOption("-o|--out <PATH>")]
        [Description("Output path. Defaults to <table>.rules.yaml; use '-' for stdout.")]
        public string? OutPath { get; init; }

        [CommandOption("-r|--rows <N>")]
        [Description("Row count written into the scaffold. Default: 100.")]
        public int Rows { get; init; } = 100;

        [CommandOption("-s|--seed <N>")]
        [Description("Seed written into the scaffold. Default: 12345.")]
        public int Seed { get; init; } = 12345;

        [CommandOption("-f|--force")]
        [Description("Overwrite the output file if it exists.")]
        public bool Force { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.DdlPath))
        {
            Console.Error.WriteLine("error: --ddl is required.");
            return ExitCodes.ConfigError;
        }
        if (!File.Exists(settings.DdlPath))
        {
            Console.Error.WriteLine($"error: DDL file not found: {settings.DdlPath}");
            return ExitCodes.ConfigError;
        }

        var table = DdlParser.ParseSingle(File.ReadAllText(settings.DdlPath), settings.Table);
        var yaml = RuleScaffolder.Scaffold(table, settings.Rows, settings.Seed);

        if (settings.OutPath == "-")
        {
            Console.Out.Write(yaml);
            return ExitCodes.Ok;
        }

        var outPath = settings.OutPath ?? $"{table.Name}.rules.yaml";
        if (File.Exists(outPath) && !settings.Force)
        {
            Console.Error.WriteLine($"error: {outPath} already exists; use --force to overwrite.");
            return ExitCodes.ConfigError;
        }

        File.WriteAllText(outPath, yaml);
        Console.WriteLine($"Scaffolded rules for {table.Schema}.{table.Name} " +
                          $"({table.Columns.Count} columns) -> {outPath}");
        Console.WriteLine("Review the strategies, then run: synthgen generate " +
                          $"--ddl \"{settings.DdlPath}\" --rules \"{outPath}\"");
        return ExitCodes.Ok;
    }
}
