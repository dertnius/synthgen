using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Pfandwerk.Core.Rules;

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
            throw new GapRulesLoadException(
                $"Rule '{rule.Id}': '{field}' contains a statement terminator.");

        // Wrap the predicate in a throwaway SELECT so ScriptDom parses it as a boolean
        // expression in the position it will actually occupy.
        var probe = $"SELECT 1 FROM {rule.Table} WHERE {predicate}";
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(probe), out var errors);

        if (errors.Count > 0)
            throw new GapRulesLoadException(
                $"Rule '{rule.Id}': '{field}' is not a valid boolean expression — " +
                string.Join("; ", errors.Select(e => e.Message)));

        var script = (TSqlScript)fragment;
        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count != 1)
            throw new GapRulesLoadException(
                $"Rule '{rule.Id}': '{field}' expands to {statements.Count} statements; exactly one is allowed.");

        var visitor = new TableCollector();
        fragment.Accept(visitor);

        var allowed = Normalize(rule.Table);
        var foreign = visitor.Tables.Where(t => !string.Equals(t, allowed, StringComparison.OrdinalIgnoreCase))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
        if (foreign.Count > 0)
            throw new GapRulesLoadException(
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
