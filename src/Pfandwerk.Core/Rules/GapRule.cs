using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Pfandwerk.Core.Rules;

public sealed class GapRulesLoadException : Exception
{
    public GapRulesLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

public enum RuleKind { Ephemeral, Identity, Derived }

/// <summary>How a derived rule behaves when a declared input is unusable.</summary>
public enum MissingInputPolicy
{
    /// <summary>Leave the row unpatched and list it at the gate so a human decides.</summary>
    Block,
    /// <summary>Patch it with the generator's documented minimum.</summary>
    Floor,
}

public sealed class GapRulesFile
{
    public List<GapRule> Rules { get; set; } = new();
}

/// <summary>One bad-data pattern and how to repair it. See rules/gaps.yaml for field docs.</summary>
public sealed class GapRule
{
    public string Id { get; set; } = "";
    public string Table { get; set; } = "";
    public string Key { get; set; } = "";
    public string Column { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Gap { get; set; } = "";
    public string Fix { get; set; } = "";
    public int Threshold { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Derived rules only: columns the generator may read.</summary>
    public List<string>? Inputs { get; set; }

    /// <summary>Derived rules only: block (default) | floor.</summary>
    public string? OnMissingInput { get; set; }

    /// <summary>Optional predicate that must hold for every row of the table after the run.</summary>
    public string? Invariant { get; set; }

    public RuleKind ParsedKind => Kind switch
    {
        "ephemeral" => RuleKind.Ephemeral,
        "identity" => RuleKind.Identity,
        "derived" => RuleKind.Derived,
        _ => throw new GapRulesLoadException($"Rule '{Id}': unknown kind '{Kind}'."),
    };

    public MissingInputPolicy ParsedMissingInputPolicy => (OnMissingInput ?? "block") switch
    {
        "block" => MissingInputPolicy.Block,
        "floor" => MissingInputPolicy.Floor,
        var other => throw new GapRulesLoadException(
            $"Rule '{Id}': onMissingInput must be 'block' or 'floor', not '{other}'."),
    };
}

public static class GapRulesLoader
{
    public static GapRulesFile LoadFile(string path)
    {
        if (!File.Exists(path)) throw new GapRulesLoadException($"Rules file not found: {path}");
        return Load(File.ReadAllText(path));
    }

    public static GapRulesFile Load(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        GapRulesFile file;
        try
        {
            file = deserializer.Deserialize<GapRulesFile>(yaml)
                   ?? throw new GapRulesLoadException("Rules file is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new GapRulesLoadException($"Rules YAML is invalid at {ex.Start}: {ex.Message}", ex);
        }

        Validate(file);
        return file;
    }

    private static void Validate(GapRulesFile file)
    {
        if (file.Rules.Count == 0) throw new GapRulesLoadException("Rules file declares no rules.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in file.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) throw new GapRulesLoadException("Every rule needs an 'id'.");
            if (!seen.Add(r.Id)) throw new GapRulesLoadException($"Duplicate rule id '{r.Id}'.");

            foreach (var (name, value) in new[]
                     { ("table", r.Table), ("key", r.Key), ("column", r.Column),
                       ("kind", r.Kind), ("gap", r.Gap), ("fix", r.Fix), ("reason", r.Reason) })
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new GapRulesLoadException($"Rule '{r.Id}': '{name}' is required.");
            }

            _ = r.ParsedKind;
            if (r.Threshold <= 0)
                throw new GapRulesLoadException($"Rule '{r.Id}': 'threshold' must be positive.");

            if (r.ParsedKind == RuleKind.Derived)
            {
                if (r.Inputs is null || r.Inputs.Count == 0)
                    throw new GapRulesLoadException($"Rule '{r.Id}': derived rules require 'inputs'.");
                _ = r.ParsedMissingInputPolicy;
            }
            else if (r.Inputs is not null || r.OnMissingInput is not null)
            {
                throw new GapRulesLoadException(
                    $"Rule '{r.Id}': 'inputs' and 'onMissingInput' apply only to derived rules.");
            }

            GapPredicateValidator.Validate(r);
        }
    }
}
