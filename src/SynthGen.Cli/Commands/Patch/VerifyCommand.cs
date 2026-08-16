using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

[Description("Run the three verify layers against the baseline and write verify.json.")]
public sealed class VerifyCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        var result = new Verifier(o.Db(), o.LoadRules()).Verify(
            Json.Read<PlanDocument>(o.Path("plan.json")),
            Json.Read<BaselineDocument>(o.Path("baseline.json")),
            o.ConsumerChecksOrEmpty());
        Json.Write(o.Path("verify.json"), result);

        Console.WriteLine($"VERIFY L1 {P(result.L1)}  L2 {P(result.L2)}  L3 {P(result.L3)}");
        Console.WriteLine($"  regressions:            {List(result.Regressions)}");
        Console.WriteLine($"  pre-existing failures:  {List(result.PreexistingReds)}");
        return Verifier.ExitCodeFor(result);

        static string P(bool ok) => ok ? "pass" : "FAIL";
        static string List(List<string> xs) => xs.Count == 0 ? "none" : string.Join(", ", xs);
    }
}
