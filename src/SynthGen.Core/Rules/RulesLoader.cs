using SynthGen.Core.Support;

namespace SynthGen.Core.Rules;

public sealed class RulesLoadException : Exception
{
    public RulesLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class RulesLoader
{
    public static RulesFile LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new RulesLoadException($"Rules file not found: {path}");
        return Load(File.ReadAllText(path));
    }

    public static RulesFile Load(string yaml)
    {
        var deserializer = Yaml.Deserializer();

        RulesFile rules;
        try
        {
            rules = deserializer.Deserialize<RulesFile>(yaml)
                ?? throw new RulesLoadException("Rules file is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new RulesLoadException($"Rules YAML is invalid at {ex.Start}: {ex.Message}", ex);
        }

        Validate(rules);
        return rules;
    }

    private static void Validate(RulesFile rules)
    {
        if (rules.Rows <= 0)
            throw new RulesLoadException("'rows' must be a positive integer.");
        if (rules.BatchSize <= 0)
            throw new RulesLoadException("'batchSize' must be a positive integer.");

        foreach (var (column, rule) in rules.Columns)
        {
            if (rule is null)
                throw new RulesLoadException($"Column '{column}' has an empty rule; remove it or set a strategy.");
            if (rule.NullRate is < 0 or > 1)
                throw new RulesLoadException($"Column '{column}': nullRate must be between 0 and 1.");
            if (rule.TrueRate is < 0 or > 1)
                throw new RulesLoadException($"Column '{column}': trueRate must be between 0 and 1.");
            if (rule.Weights is not null && rule.Values is not null && rule.Weights.Count != rule.Values.Count)
                throw new RulesLoadException($"Column '{column}': weights count must match values count.");
            if (rule.Strategy == "pick" && (rule.Values is null || rule.Values.Count == 0))
                throw new RulesLoadException($"Column '{column}': strategy 'pick' requires a non-empty 'values' list.");
            if (rule.Strategy == "template" && string.IsNullOrEmpty(rule.Template))
                throw new RulesLoadException($"Column '{column}': strategy 'template' requires 'template'.");
            if (rule.Strategy == "faker" && string.IsNullOrEmpty(rule.Method))
                throw new RulesLoadException($"Column '{column}': strategy 'faker' requires 'method'.");
            if (rule.Strategy == "query" && string.IsNullOrEmpty(rule.Query))
                throw new RulesLoadException($"Column '{column}': strategy 'query' requires 'query'.");
            if (rule.Strategy == "constant" && rule.Value is null)
                throw new RulesLoadException($"Column '{column}': strategy 'constant' requires 'value'.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var eval in rules.Evaluations)
        {
            if (string.IsNullOrWhiteSpace(eval.Name))
                throw new RulesLoadException("Every evaluation needs a 'name'.");
            if (!seenNames.Add(eval.Name))
                throw new RulesLoadException($"Duplicate evaluation name '{eval.Name}'.");
            if (string.IsNullOrWhiteSpace(eval.Query))
                throw new RulesLoadException($"Evaluation '{eval.Name}' needs a 'query'.");

            var e = eval.Expect;
            if (e is not null)
            {
                int set = (e.EqualsValue is not null ? 1 : 0)
                        + (e.Min is not null || e.Max is not null ? 1 : 0)
                        + (e.Between is not null ? 1 : 0);
                if (set > 1)
                    throw new RulesLoadException(
                        $"Evaluation '{eval.Name}': use only one of equals, min/max, or between.");
                if (e.Between is not null && e.Between.Count != 2)
                    throw new RulesLoadException($"Evaluation '{eval.Name}': 'between' needs exactly [low, high].");
            }
        }
    }
}
