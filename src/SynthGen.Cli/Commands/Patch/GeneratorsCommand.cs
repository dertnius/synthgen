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

        var datasets = o.Datasets();
        Console.WriteLine("\nDataset keys — ephemeral (random row) or derived (row looked up by `inputs`)");
        if (datasets.All.Any())
        {
            foreach (var d in datasets.All)
                Console.WriteLine($"  dataset.{d.Name}  (columns: {string.Join(", ", d.Columns)})");
        }
        else
        {
            Console.WriteLine("  none — add a YAML vocabulary under " +
                              $"{System.IO.Path.GetDirectoryName(o.Rules)}/datasets/");
        }

        Console.WriteLine("\nNeed one that is not listed? A vocabulary is a reviewed dataset file; " +
                          "only logic goes to Generators.cs. Either way it arrives by MR.");
        return ExitCodes.Ok;
    }
}
