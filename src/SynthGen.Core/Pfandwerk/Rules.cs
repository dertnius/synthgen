using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Pfandwerk;

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
        _ => throw new RulesLoadException($"Rule '{Id}': unknown kind '{Kind}'."),
    };

    public MissingInputPolicy ParsedMissingInputPolicy => (OnMissingInput ?? "block") switch
    {
        "block" => MissingInputPolicy.Block,
        "floor" => MissingInputPolicy.Floor,
        var other => throw new RulesLoadException(
            $"Rule '{Id}': onMissingInput must be 'block' or 'floor', not '{other}'."),
    };
}

public static class GapRulesLoader
{
    public static GapRulesFile LoadFile(string path)
    {
        if (!File.Exists(path)) throw new RulesLoadException($"Rules file not found: {path}");
        return Load(File.ReadAllText(path));
    }

    public static GapRulesFile Load(string yaml)
    {
        var deserializer = Yaml.Deserializer();

        GapRulesFile file;
        try
        {
            file = deserializer.Deserialize<GapRulesFile>(yaml)
                   ?? throw new RulesLoadException("Rules file is empty.");
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new RulesLoadException($"Rules YAML is invalid at {ex.Start}: {ex.Message}", ex);
        }

        Validate(file);
        return file;
    }

    private static void Validate(GapRulesFile file)
    {
        if (file.Rules.Count == 0) throw new RulesLoadException("Rules file declares no rules.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in file.Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id)) throw new RulesLoadException("Every rule needs an 'id'.");
            if (!seen.Add(r.Id)) throw new RulesLoadException($"Duplicate rule id '{r.Id}'.");

            foreach (var (name, value) in new[]
                     { ("table", r.Table), ("key", r.Key), ("column", r.Column),
                       ("kind", r.Kind), ("gap", r.Gap), ("fix", r.Fix), ("reason", r.Reason) })
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new RulesLoadException($"Rule '{r.Id}': '{name}' is required.");
            }

            _ = r.ParsedKind;
            if (r.Threshold <= 0)
                throw new RulesLoadException($"Rule '{r.Id}': 'threshold' must be positive.");

            if (r.ParsedKind == RuleKind.Derived)
            {
                if (r.Inputs is null || r.Inputs.Count == 0)
                    throw new RulesLoadException($"Rule '{r.Id}': derived rules require 'inputs'.");
                _ = r.ParsedMissingInputPolicy;
            }
            else if (r.Inputs is not null || r.OnMissingInput is not null)
            {
                throw new RulesLoadException(
                    $"Rule '{r.Id}': 'inputs' and 'onMissingInput' apply only to derived rules.");
            }

            // Resolve the generator now, not when a row first happens to match. A typo'd
            // fix key would otherwise sit dormant until the day the data goes bad, which is
            // precisely the day nobody wants to debug the rules file.
            var derived = PatchGenerators.IsDerived(r.Fix);
            if (r.ParsedKind == RuleKind.Derived && !derived)
                throw new RulesLoadException(
                    $"Rule '{r.Id}': '{r.Fix}' is not a derived generator. " +
                    $"Available: {string.Join(", ", PatchGenerators.DerivedKeys)}");
            if (r.ParsedKind != RuleKind.Derived && derived)
                throw new RulesLoadException(
                    $"Rule '{r.Id}': '{r.Fix}' is a derived generator and needs kind: derived.");
            if (!derived && !PatchGenerators.RandomKeys.Contains(r.Fix, StringComparer.OrdinalIgnoreCase))
                throw new RulesLoadException(
                    $"Rule '{r.Id}': unknown fix key '{r.Fix}'. Run `synthgen patch generators` for the list.");

            GapPredicateValidator.Validate(r);
        }
    }
}


/// <summary>
/// Parses a rule's gap/invariant predicate with ScriptDom and rejects anything that could
/// reach outside the rule's declared table. Hard rule 4 is enforced here rather than by
/// convention: without an AST walk, a subquery inside a predicate could read any table in
/// the instance.
/// </summary>
public static class GapPredicateValidator
{
    public static void Validate(GapRule rule)
    {
        Check(rule, rule.Gap, "gap");
        if (!string.IsNullOrWhiteSpace(rule.Invariant)) Check(rule, rule.Invariant!, "invariant");
    }

    private static void Check(GapRule rule, string predicate, string field)
    {
        if (predicate.Contains(';'))
            throw new RulesLoadException(
                $"Rule '{rule.Id}': '{field}' contains a statement terminator.");

        // Wrap the predicate in a throwaway SELECT so ScriptDom parses it as a boolean
        // expression in the position it will actually occupy.
        var probe = $"SELECT 1 FROM {rule.Table} WHERE {predicate}";
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(probe), out var errors);

        if (errors.Count > 0)
            throw new RulesLoadException(
                $"Rule '{rule.Id}': '{field}' is not a valid boolean expression — " +
                string.Join("; ", errors.Select(e => e.Message)));

        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1)
            throw new RulesLoadException(
                $"Rule '{rule.Id}': '{field}' expands to {statements.Count} statements; exactly one is allowed.");

        var visitor = new TableCollector();
        fragment.Accept(visitor);

        var allowed = Normalize(rule.Table);
        var foreign = visitor.Tables.Where(t => !string.Equals(t, allowed, StringComparison.OrdinalIgnoreCase))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
        if (foreign.Count > 0)
            throw new RulesLoadException(
                $"Rule '{rule.Id}': '{field}' references {string.Join(", ", foreign)}, " +
                $"but the rule declares only {rule.Table}. Only tables listed in the rule may be touched.");
    }

    /// <summary>Schema-qualified, unbracketed, for comparison.</summary>
    private static string Normalize(string name) =>
        string.Join('.', name.Split('.').Select(p => p.Trim('[', ']', '"')));

    private sealed class TableCollector : TSqlFragmentVisitor
    {
        public List<string> Tables { get; } = new();

        public override void Visit(NamedTableReference node)
        {
            var parts = node.SchemaObject.Identifiers.Select(i => i.Value);
            Tables.Add(string.Join('.', parts));
        }
    }
}


/// <summary>
/// The single place a gap predicate becomes SQL. SCAN, PLAN and VERIFY layer 1 all call
/// this — three independent implementations would eventually disagree about what a gap is,
/// and the symptom would be a run reporting success while gaps remain open.
/// </summary>
public static class GapQuery
{
    public static string Count(GapRule rule) =>
        $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.Gap}";

    /// <summary>Key, current value, and any declared inputs, for every matching row.</summary>
    public static string Rows(GapRule rule)
    {
        var columns = new List<string> { rule.Key, rule.Column };
        if (rule.Inputs is not null) columns.AddRange(rule.Inputs);
        var distinct = columns.Distinct(StringComparer.OrdinalIgnoreCase);
        return $"SELECT {string.Join(", ", distinct)} FROM {rule.Table} WHERE {rule.Gap} ORDER BY {rule.Key}";
    }

    /// <summary>Rows still matching the gap among a specific planned key set (VERIFY layer 1).</summary>
    public static string RemainingAmong(GapRule rule, IEnumerable<string> keys)
    {
        var list = string.Join(", ", keys.Select(Literal));
        return $"SELECT {rule.Key} FROM {rule.Table} WHERE ({rule.Gap}) AND {rule.Key} IN ({list})";
    }

    /// <summary>Rows violating the rule's invariant, across the whole table.</summary>
    public static string InvariantViolations(GapRule rule) =>
        $"SELECT {rule.Key} FROM {rule.Table} WHERE NOT ({rule.Invariant}) ORDER BY {rule.Key}";

    /// <summary>Rows the invariant positively holds for. Needed to count the rest.</summary>
    public static string InvariantHolds(GapRule rule) =>
        $"SELECT {rule.Key} FROM {rule.Table} WHERE ({rule.Invariant}) ORDER BY {rule.Key}";

    /// <summary>
    /// Rows that break the invariant but the gap does not select — the predicate is too
    /// narrow to reach a defect it claims to repair.
    ///
    /// <para>Uses <c>key NOT IN (gap set)</c> rather than <c>NOT (gap)</c> on purpose.
    /// <c>NOT (gap)</c> is UNKNOWN wherever the predicate touches a NULL, so exactly the
    /// rows worth surfacing would vanish from the result. The subquery returns only rows
    /// where the gap is definitely TRUE, and the key column is non-null by definition,
    /// which makes NOT IN safe here.</para>
    /// </summary>
    public static string UncoveredViolations(GapRule rule) =>
        $"SELECT {rule.Key} FROM {rule.Table} WHERE NOT ({rule.Invariant}) " +
        $"AND {rule.Key} NOT IN (SELECT {rule.Key} FROM {rule.Table} WHERE {rule.Gap}) " +
        $"ORDER BY {rule.Key}";

    /// <summary>
    /// Rows the gap selects that already satisfy the invariant — the predicate is too broad
    /// and this run would patch rows that were fine.
    /// </summary>
    public static string SelectedButValid(GapRule rule) =>
        $"SELECT {rule.Key} FROM {rule.Table} WHERE ({rule.Gap}) AND ({rule.Invariant}) " +
        $"ORDER BY {rule.Key}";

    public static string Update(GapRule rule, string keyParam, string valueParam) =>
        $"UPDATE {rule.Table} SET {rule.Column} = {valueParam} WHERE {rule.Key} = {keyParam}";

    /// <summary>Keys arrive as strings from artifacts; numeric ones must not be quoted.</summary>
    private static string Literal(string key) =>
        long.TryParse(key, out _) ? key : "'" + key.Replace("'", "''") + "'";
}
