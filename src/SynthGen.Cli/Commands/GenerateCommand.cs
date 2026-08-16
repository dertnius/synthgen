using System.ComponentModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Spectre.Console.Cli;
using SynthGen.Core.Ddl;
using SynthGen.Core.Eval;
using SynthGen.Core.Generation;
using SynthGen.Core.Load;
using SynthGen.Core.Rules;
using SynthGen.Sqlite;

namespace SynthGen.Cli.Commands;

[Description("Generate synthetic rows from DDL + rules and load them (SqlBulkCopy), " +
             "then run the evaluations defined in the rules file.")]
public sealed class GenerateCommand : Command<GenerateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-d|--ddl <PATH>")]
        [Description("Path to the .sql file containing the CREATE TABLE statement.")]
        public string DdlPath { get; init; } = "";

        [CommandOption("-u|--rules <PATH>")]
        [Description("Path to the rules YAML.")]
        public string RulesPath { get; init; } = "";

        [CommandOption("-t|--table <NAME>")]
        [Description("Table to generate when the DDL defines several.")]
        public string? Table { get; init; }

        [CommandOption("-c|--connection <CONNSTR>")]
        [Description("SQL Server connection string; falls back to SYNTHGEN_CONNECTION.")]
        public string? Connection { get; init; }

        [CommandOption("-r|--rows <N>")]
        [Description("Override the row count from the rules file.")]
        public int? Rows { get; init; }

        [CommandOption("-s|--seed <N>")]
        [Description("Override the seed from the rules file.")]
        public int? Seed { get; init; }

        [CommandOption("--truncate")]
        [Description("Delete existing rows before loading (overrides truncateBeforeLoad: false).")]
        public bool Truncate { get; init; }

        [CommandOption("--csv <PATH>")]
        [Description("Write rows to CSV instead of the database.")]
        public string? CsvPath { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print a sample of generated rows; no database writes.")]
        public bool DryRun { get; init; }

        [CommandOption("--sample <N>")]
        [Description("Sample size for --dry-run. Default: 10.")]
        public int Sample { get; init; } = 10;

        [CommandOption("--no-evaluate")]
        [Description("Skip the evaluations after loading.")]
        public bool NoEvaluate { get; init; }

        [CommandOption("--json")]
        [Description("Emit a machine-readable JSON report on stdout.")]
        public bool Json { get; init; }

        [CommandOption("-p|--provider <NAME>")]
        [Description("Database provider: sqlserver (default) or sqlite (local smoke tests; " +
                     "needs a conda/micromamba-installed sqlite3).")]
        public string Provider { get; init; } = "sqlserver";

        [CommandOption("--create-table")]
        [Description("sqlite only: create the tables from the DDL before loading.")]
        public bool CreateTable { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.DdlPath) || string.IsNullOrWhiteSpace(settings.RulesPath))
        {
            Console.Error.WriteLine("error: --ddl and --rules are required.");
            return ExitCodes.ConfigError;
        }
        if (!File.Exists(settings.DdlPath))
        {
            Console.Error.WriteLine($"error: DDL file not found: {settings.DdlPath}");
            return ExitCodes.ConfigError;
        }

        var rules = RulesLoader.LoadFile(settings.RulesPath);
        if (settings.Rows is not null) rules.Rows = settings.Rows.Value;
        if (settings.Seed is not null) rules.Seed = settings.Seed.Value;
        if (settings.Truncate) rules.TruncateBeforeLoad = true;

        var tableSelector = settings.Table ?? rules.Table;
        var ddlText = File.ReadAllText(settings.DdlPath);
        var tables = DdlParser.ParseScript(ddlText);
        var table = tables.Count == 1 && tableSelector is null
            ? tables[0]
            : DdlParser.ParseSingle(ddlText, tableSelector);

        var plan = GenerationPlan.Build(table, rules);
        foreach (var warning in plan.Warnings)
            Console.Error.WriteLine($"warning: {warning}");

        var connection = CliSupport.ResolveConnection(settings.Connection);
        bool offline = settings.DryRun || settings.CsvPath is not null;
        bool useSqlite = settings.Provider.Equals("sqlite", StringComparison.OrdinalIgnoreCase);
        if (!useSqlite && !settings.Provider.Equals("sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"error: unknown provider '{settings.Provider}'. Use sqlserver or sqlite.");
            return ExitCodes.ConfigError;
        }
        if (settings.CreateTable && !useSqlite)
        {
            Console.Error.WriteLine("error: --create-table is only supported with --provider sqlite; " +
                                    "SQL Server schemas are managed outside this tool.");
            return ExitCodes.ConfigError;
        }

        if (plan.Lookups.Count > 0 && connection is null)
        {
            Console.Error.WriteLine(
                "error: these columns use the 'query' strategy and need a database connection " +
                $"even for CSV/dry runs: {string.Join(", ", plan.Lookups.Keys)}. " +
                $"Pass --connection or set {CliSupport.ConnectionEnvVar}.");
            return ExitCodes.ConfigError;
        }
        if (!offline && connection is null)
        {
            Console.Error.WriteLine(
                $"error: no connection string. Pass --connection or set {CliSupport.ConnectionEnvVar}, " +
                "or use --csv/--dry-run for an offline run.");
            return ExitCodes.ConfigError;
        }

        Action<long> progress = total => Console.Error.WriteLine($"  loaded {total} rows...");
        Func<IDbConnection>? connectionFactory = null;
        ITableLoader? loader = null;

        if (connection is not null)
        {
            if (useSqlite)
            {
                if (!SqliteNative.TryInitialize(out var sqliteError))
                {
                    Console.Error.WriteLine($"error: {sqliteError}");
                    return ExitCodes.ConfigError;
                }
                var sqliteFactory = new SqliteConnectionFactory(
                    CliSupport.ExtractSqliteDataSource(connection),
                    tables.Select(t => t.Schema));
                if (settings.CreateTable)
                {
                    using var schemaConn = sqliteFactory.Open();
                    foreach (var t in tables)
                    {
                        using var create = schemaConn.CreateCommand();
                        create.CommandText = SqliteSchemaBuilder.BuildCreateTable(t);
                        create.ExecuteNonQuery();
                    }
                    Console.Error.WriteLine($"  ensured {tables.Count} table(s) in {sqliteFactory.DatabasePath}");
                }
                connectionFactory = () => sqliteFactory.Open();
                loader = new SqliteTableWriter(sqliteFactory) { OnBatchLoaded = progress };
            }
            else
            {
                connectionFactory = () => new SqlConnection(connection);
                loader = new BulkLoader(connection) { OnBatchLoaded = progress };
            }
        }

        var lookups = plan.Lookups.Count > 0
            ? LookupFetcher.Fetch(plan, connectionFactory!)
            : new Dictionary<string, IReadOnlyList<object>>();

        var generator = new RowGenerator(plan, lookups);

        if (settings.DryRun)
            return RunDrySample(generator, settings.Sample);

        if (settings.CsvPath is not null)
        {
            long written = CsvWriter.WriteFile(generator, settings.CsvPath);
            Console.WriteLine($"Wrote {written} rows to {settings.CsvPath} (seed {generator.EffectiveSeed}).");
            ReportTruncations(generator);
            return ExitCodes.Ok;
        }

        var result = loader!.Load(generator, rules.TruncateBeforeLoad);
        ReportTruncations(generator);

        List<EvaluationResult>? evaluations = null;
        if (!settings.NoEvaluate && rules.Evaluations.Count > 0)
            evaluations = new Evaluator(connectionFactory!).Run(rules.Evaluations);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Table = table.QualifiedName,
                RowsLoaded = result.RowsLoaded,
                Seed = generator.EffectiveSeed,
                ElapsedMs = (long)result.Elapsed.TotalMilliseconds,
                KeepIdentity = generator.KeepIdentity,
                Warnings = plan.Warnings,
                Truncations = generator.Truncations,
                Evaluations = evaluations,
                Passed = evaluations is null || CliSupport.AllPassed(evaluations),
            }, Json.Options));
        }
        else
        {
            Console.WriteLine($"Loaded {result.RowsLoaded} rows into {table.QualifiedName} " +
                              $"in {result.Elapsed.TotalSeconds:F1}s (seed {generator.EffectiveSeed}).");
            if (evaluations is not null)
            {
                Console.WriteLine("Evaluations:");
                CliSupport.PrintEvaluations(evaluations, Console.Out);
            }
        }

        return evaluations is not null && !CliSupport.AllPassed(evaluations)
            ? ExitCodes.EvaluationFailed
            : ExitCodes.Ok;
    }

    private int RunDrySample(RowGenerator generator, int sample)
    {
        var names = generator.Columns.Select(c => c.Column.Name).ToArray();
        Console.WriteLine(string.Join(" | ", names));
        foreach (var row in generator.Rows().Take(sample))
        {
            Console.WriteLine(string.Join(" | ", row.Select(v => v switch
            {
                null => "NULL",
                byte[] b => $"0x{Convert.ToHexString(b)}",
                _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
            })));
        }
        Console.WriteLine($"({sample} of {generator.RowCount} rows, seed {generator.EffectiveSeed}, dry run — nothing written)");
        return ExitCodes.Ok;
    }

    private static void ReportTruncations(RowGenerator generator)
    {
        foreach (var (column, count) in generator.Truncations)
            Console.Error.WriteLine(
                $"warning: {count} value(s) in column '{column}' were truncated to the DDL length.");
    }
}
