using Bogus;
using Dapper;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Generation;
using Pfandwerk.Core.Rules;

namespace Pfandwerk.Core.Phases;

/// <summary>
/// Produces the plan. Every value — ephemeral, identity and derived alike — is generated
/// here and frozen into plan.json, so the SHA-256 the reviewer approves covers the values
/// that will actually be written, and APPLY calls no generator at all.
/// </summary>
public sealed class Planner
{
    private readonly DbContext _db;
    private readonly LedgerRepository _ledger;
    private readonly List<GapRule> _rules;
    private readonly Faker _faker;

    public Planner(DbContext db, LedgerRepository ledger, List<GapRule> rules, int? seed = null)
    {
        (_db, _ledger, _rules) = (db, ledger, rules);
        _faker = seed is null ? new Faker() : new Faker { Random = new Randomizer(seed.Value) };
    }

    public PlanDocument Plan(GapsDocument gaps)
    {
        var plans = new List<RulePlan>();

        foreach (var rule in _rules)
        {
            var scanned = gaps.Rules.FirstOrDefault(g => g.Id == rule.Id);
            if (scanned is null) continue;

            var patches = new List<PlannedPatch>();
            var skipped = new List<SkippedRow>();
            var identities = new List<NewIdentity>();

            var status = scanned.Count > rule.Threshold ? "BLOCKED" : "OK";
            if (status == "OK")
            {
                foreach (var row in scanned.Rows)
                {
                    var rowKey = row.Key[rule.Key];
                    switch (rule.ParsedKind)
                    {
                        case RuleKind.Identity:
                            var value = MintIdentity(rule, rowKey);
                            patches.Add(new PlannedPatch(row.Key, value, null));
                            identities.Add(new NewIdentity(row.Key, value));
                            break;

                        case RuleKind.Derived:
                            var derived = Derive(rule, row, out var why);
                            if (derived is null)
                                skipped.Add(new SkippedRow(row.Key, why!, $"onMissingInput: {rule.OnMissingInput ?? "block"}"));
                            else
                                patches.Add(new PlannedPatch(row.Key, derived, row.Inputs));
                            break;

                        default:
                            patches.Add(new PlannedPatch(row.Key,
                                Canonical.Format(PatchGenerators.Random(rule.Fix, _faker)), null));
                            break;
                    }
                }
            }

            plans.Add(new RulePlan(rule.Id, rule.Table, rule.Column, rule.Kind, status,
                                   scanned.Count, rule.Threshold, rule.Reason,
                                   patches, skipped, identities));
        }

        return new PlanDocument(gaps.RunId, gaps.RulesSha, plans);
    }

    /// <summary>
    /// Ledger hit reuses the recorded value — that is what "frozen forever" means. A miss
    /// generates and then collision-checks against the live table *and* the ledger before
    /// the value is accepted.
    /// </summary>
    private string MintIdentity(GapRule rule, string rowKey)
    {
        var existing = _ledger.Lookup(rule.Table, rowKey, rule.Column);
        if (existing is not null) return existing;

        using var conn = _db.OpenTarget();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Canonical.Format(PatchGenerators.Random(rule.Fix, _faker));

            var inTable = conn.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.Column} = @v", new { v = candidate }) > 0;
            if (inTable) continue;
            if (_ledger.ValueTaken(rule.Table, rule.Column, candidate)) continue;

            return candidate;
        }
        throw new GeneratorException(
            $"Rule '{rule.Id}': could not generate a collision-free value for {rule.Column} in 100 attempts.");
    }

    private string? Derive(GapRule rule, GapRow row, out string? why)
    {
        why = null;
        var inputs = (row.Inputs ?? new Dictionary<string, string?>())
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        var missing = rule.Inputs!.Where(i => inputs.GetValueOrDefault(i) is null).ToList();
        if (missing.Count > 0)
        {
            if (rule.ParsedMissingInputPolicy == MissingInputPolicy.Floor)
                return Canonical.Format(PatchGenerators.Floor(rule.Fix));

            why = $"input '{string.Join("', '", missing)}' is NULL";
            return null;
        }

        return Canonical.Format(PatchGenerators.Derived(rule.Fix, inputs));
    }
}
