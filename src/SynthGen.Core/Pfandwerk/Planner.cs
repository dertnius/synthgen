using System.Data;
using Bogus;
using Dapper;

namespace Pfandwerk;

/// <summary>
/// Finds the gaps and decides every value, in one pass. Each value — ephemeral, identity
/// and derived alike — is frozen into plan.json here, so the SHA-256 a reviewer approves
/// covers what will actually be written and APPLY calls no generator at all.
/// </summary>
public sealed class Planner
{
    /// <summary>Sample keys shown per finding, matching how the gate truncates tier 2.</summary>
    private const int Samples = 5;

    private readonly DbContext _db;
    private readonly LedgerRepository _ledger;
    private readonly List<GapRule> _rules;
    private readonly Faker _faker;
    private readonly HashSet<string> _plannedIdentityValues = new(StringComparer.Ordinal);

    public Planner(DbContext db, LedgerRepository ledger, List<GapRule> rules, int? seed = null)
    {
        (_db, _ledger, _rules) = (db, ledger, rules);
        _faker = seed is null ? new Faker() : new Faker { Random = new Randomizer(seed.Value) };
    }

    public PlanDocument Plan(string runId, string rulesSha)
    {
        _plannedIdentityValues.Clear();
        using var conn = _db.OpenTarget();
        var plans = new List<RulePlan>();

        foreach (var rule in _rules)
        {
            ValidateAgainstSchema(conn, rule);

            var count = conn.ExecuteScalar<int>(GapQuery.Count(rule));
            var patches = new List<PlannedPatch>();
            var skipped = new List<SkippedRow>();
            var identities = new List<NewIdentity>();

            // Past its threshold a rule is BLOCKED and the gate refuses the whole run, so
            // there is nothing to plan for it.
            var status = count > rule.Threshold ? "BLOCKED" : "OK";
            if (status == "OK")
            {
                foreach (IDictionary<string, object?> row in conn.Query(GapQuery.Rows(rule)))
                {
                    var key = new Dictionary<string, string> { [rule.Key] = Canonical.Format(row[rule.Key]) };
                    var rowKey = key[rule.Key];

                    switch (rule.ParsedKind)
                    {
                        case RuleKind.Identity:
                            var minted = MintIdentity(conn, rule, rowKey);
                            patches.Add(new PlannedPatch(key, minted, null));
                            identities.Add(new NewIdentity(key, minted));
                            break;

                        case RuleKind.Derived:
                            var inputs = rule.Inputs!.ToDictionary(
                                i => i, i => row[i] is null ? null : Canonical.Format(row[i]));
                            var derived = Derive(rule, inputs, out var why);
                            if (derived is null)
                                skipped.Add(new SkippedRow(key, why!,
                                    $"onMissingInput: {rule.OnMissingInput ?? "block"}"));
                            else
                                patches.Add(new PlannedPatch(key, derived, inputs));
                            break;

                        default:
                            patches.Add(new PlannedPatch(key,
                                Canonical.Format(PatchGenerators.Random(rule.Fix, _faker)), null));
                            break;
                    }
                }
            }

            plans.Add(new RulePlan(rule.Id, rule.Table, rule.Column, rule.Kind, status,
                                   count, rule.Threshold, rule.Reason, patches, skipped, identities,
                                   Coverage(conn, rule)));
        }

        return new PlanDocument(runId, rulesSha, plans);
    }

    /// <summary>
    /// Cross-checks the gap predicate against the rule's own invariant. Everything else
    /// validates a rule's shape; this is the only check that asks whether it selects the
    /// rows a person meant.
    ///
    /// <para>Advisory throughout — it reports, the gate prints, nothing blocks.</para>
    /// </summary>
    private static RuleCoverage? Coverage(IDbConnection conn, GapRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Invariant)) return null;

        var uncovered = conn.Query<string>(GapQuery.UncoveredViolations(rule)).ToList();
        var selectedValid = conn.Query<string>(GapQuery.SelectedButValid(rule)).ToList();
        var (indeterminate, indeterminateKeys) = Indeterminate.Rows(conn, rule, Samples);

        return new RuleCoverage(
            uncovered.Count, uncovered.Take(Samples).ToList(),
            selectedValid.Count, selectedValid.Take(Samples).ToList(),
            indeterminate, indeterminateKeys);
    }

    /// <summary>
    /// A rule naming a dropped column is a config error surfaced here, not a runtime
    /// failure three phases later. Selecting zero rows still fails if a column is missing.
    /// </summary>
    private static void ValidateAgainstSchema(IDbConnection conn, GapRule rule)
    {
        var columns = new List<string> { rule.Key, rule.Column };
        if (rule.Inputs is not null) columns.AddRange(rule.Inputs);
        try
        {
            conn.ExecuteScalar($"SELECT {string.Join(", ", columns.Distinct())} FROM {rule.Table} WHERE 1 = 0");
        }
        catch (Exception ex)
        {
            throw new RulesLoadException(
                $"Rule '{rule.Id}' does not match the live schema of {rule.Table}: {ex.Message}");
        }
    }

    /// <summary>
    /// A ledger hit reuses the recorded value — that is what "frozen forever" means. A miss
    /// generates and collision-checks against the live table <em>and</em> the ledger before
    /// the value is accepted.
    /// </summary>
    private string MintIdentity(IDbConnection conn, GapRule rule, string rowKey)
    {
        var existing = _ledger.Lookup(rule.Table, rowKey, rule.Column);
        if (existing is not null) return existing;

        var candidateQuery = PatchGenerators.CandidateQuery(rule.Fix);
        var candidates = candidateQuery is null
            ? null
            : conn.Query<long>(candidateQuery).Cast<object>().ToList();
        if (candidateQuery is not null && candidates!.Count == 0)
            throw new GeneratorException(
                $"Rule '{rule.Id}': generator '{rule.Fix}' found no existing parent IDs.");

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var generated = candidates is null
                ? PatchGenerators.Random(rule.Fix, _faker)
                : PatchGenerators.Random(rule.Fix, _faker, candidates);
            var candidate = Canonical.Format(generated);
            var inTable = conn.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.Column} = @v", new { v = candidate }) > 0;
            var planned = $"{rule.Table}\u001f{rule.Column}\u001f{candidate}";
            if (inTable || _ledger.ValueTaken(rule.Table, rule.Column, candidate) ||
                _plannedIdentityValues.Contains(planned)) continue;
            _plannedIdentityValues.Add(planned);
            return candidate;
        }
        throw new GeneratorException(
            $"Rule '{rule.Id}': no collision-free value for {rule.Column} in 100 attempts.");
    }

    private static string? Derive(GapRule rule, Dictionary<string, string?> inputs, out string? why)
    {
        why = null;
        var missing = rule.Inputs!.Where(i => inputs.GetValueOrDefault(i) is null).ToList();
        if (missing.Count > 0)
        {
            if (rule.ParsedMissingInputPolicy == MissingInputPolicy.Floor)
                return Canonical.Format(PatchGenerators.Floor(rule.Fix));
            why = $"input '{string.Join("', '", missing)}' is NULL";
            return null;
        }
        return Canonical.Format(PatchGenerators.Derived(
            rule.Fix, inputs.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)));
    }
}
