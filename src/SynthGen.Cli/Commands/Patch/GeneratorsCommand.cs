using System.ComponentModel;
using Pfandwerk;
using Spectre.Console.Cli;

namespace SynthGen.Cli.Commands.Patch;

/// <summary>
/// Every usable `fix` key. A rule naming one that is not here fails at load; adding one
/// is a code change through a reviewed MR, because the rule file may never carry a
/// literal value itself.
/// </summary>
[Description("List the allowed fix keys a rule may name.")]
public sealed class GeneratorsCommand : Command<PatchSettings>
{
    protected override int Execute(CommandContext context, PatchSettings o, CancellationToken cancellationToken)
    {
        Console.WriteLine("Random generators — for kind: ephemeral and identity");
        foreach (var k in PatchGenerators.RandomKeys) Console.WriteLine($"  {k}");
        Console.WriteLine("\nDerived generators — for kind: derived (declare `inputs`)");
        foreach (var k in PatchGenerators.DerivedKeys) Console.WriteLine($"  {k}");
        Console.WriteLine("\nNeed one that is not listed? Add it to Generators.cs and open an MR.");
        return ExitCodes.Ok;
    }
}
