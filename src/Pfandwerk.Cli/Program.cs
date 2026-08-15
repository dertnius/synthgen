using System.Text.Json;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Phases;
using Pfandwerk.Core.Rules;
using SynthGen.Sqlite;
using Pfandwerk.Cli;

// pfandwerk <verb> — one CLI, one verb per phase. Deliberately not seven console projects:
// a single parsed invocation is what lets the agent permission layer allow exactly
// `pfandwerk <verb>` and nothing else.

var opts = Cli.Parse(args);
if (opts is null) return ExitCodes.ConfigError;

try
{
    return opts.Verb switch
    {
        "guard" => Verbs.Guard(opts),
        "scan" => Verbs.Scan(opts),
        "plan" => Verbs.Plan(opts),
        "approve" => Verbs.Approve(opts),
        "apply" => Verbs.Apply(opts),
        "verify" => Verbs.Verify(opts),
        "facts" => Verbs.Facts(opts),
        "audit" => Verbs.Audit(opts),
        "fixture" => Verbs.Fixture(opts),
        "revert" => Verbs.Revert(opts),
        "notary" => Verbs.Notary(opts),
        "fallback" => Verbs.Fallback(opts),
        "generators" => Verbs.Generators(opts),
        _ => Fail($"unknown verb '{opts.Verb}'. Try: fixture guard scan plan approve apply verify facts audit fallback notary revert generators"),
    };
}
catch (GapRulesLoadException ex) { return Fail(ex.Message, ExitCodes.ConfigError); }
catch (PatchAbortedException ex) { return Fail(ex.Message, ExitCodes.ConfigError); }
catch (LedgerConflictException ex) { return Fail(ex.Message, ExitCodes.LedgerConflict); }
catch (Exception ex) { return Fail(ex.Message, ExitCodes.RuntimeError); }

static int Fail(string message, int code = ExitCodes.ConfigError)
{
    Console.Error.WriteLine($"error: {message}");
    return code;
}
