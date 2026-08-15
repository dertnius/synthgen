using System.Data;
using System.Globalization;
using System.Text.Json;
using Bogus;
using Dapper;

namespace Pfandwerk;

// SCAN, PLAN, APPLY and VERIFY. Scanning and planning are one pass: the rows a rule matches
// are only ever needed in order to plan their values, so there is no artifact between them.

/// <summary>
/// A SQL assertion that must hold. Serves both the invariant layer (generated from each
/// rule's `invariant`) and the consumer layer (declared in consumer-checks.yaml), so the
/// baseline and the post-run comparison are produced by one code path — two
/// implementations would guarantee phantom regressions.
/// </summary>
public sealed record Check(string Name, string Query, int Expected);

public static class CheckRunner
{
    public static List<CheckResult> Run(DbContext db, IEnumerable<Check> checks)
    {
        using var conn = db.OpenTarget();
        var results = new List<CheckResult>();
        foreach (var check in checks)
        {
            try
            {
                var actual = conn.ExecuteScalar<int>(check.Query);
                results.Add(new CheckResult(check.Name, actual == check.Expected,
                    actual == check.Expected ? null : $"expected {check.Expected}, got {actual}"));
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult(check.Name, false, $"query failed: {ex.Message}"));
            }
        }
        return results;
    }

    /// <summary>One check per rule that declares an invariant: zero violating rows.</summary>
    public static List<Check> Invariants(IEnumerable<GapRule> rules) =>
        rules.Where(r => !string.IsNullOrWhiteSpace(r.Invariant))
             .Select(r => new Check($"{r.Id}.invariant",
                 $"SELECT COUNT(*) FROM ({GapQuery.InvariantViolations(r)}) v", 0))
             .ToList();
}

/// <summary>
/// Finds the gaps and decides every value, in one pass. Each value — ephemeral, identity
/// and derived alike — is frozen into plan.json here, so the SHA-256 a reviewer approves
/// covers what will actually be written and APPLY calls no generator at all.
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

    public PlanDocument Plan(string runId, string rulesSha)
    {
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
                                   count, rule.Threshold, rule.Reason, patches, skipped, identities));
        }

        return new PlanDocument(runId, rulesSha, plans);
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
            throw new GapRulesLoadException(
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

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Canonical.Format(PatchGenerators.Random(rule.Fix, _faker));
            var inTable = conn.ExecuteScalar<int>(
                $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.Column} = @v", new { v = candidate }) > 0;
            if (inTable || _ledger.ValueTaken(rule.Table, rule.Column, candidate)) continue;
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

// ------------------------------------------------------------------ APPLY

public sealed class PatchAbortedException : Exception
{
    public PatchAbortedException(string message) : base(message) { }
}

/// <summary>
/// The ledger already records a different value for this row and column. Because ledger
/// rows are never updated, this cannot be reconciled automatically.
/// </summary>
public sealed class LedgerConflictException : Exception
{
    public LedgerConflictException(string message) : base(message) { }
}

/// <summary>A single column write. <c>Value</c> is null when reverting to NULL.</summary>
public sealed record PatchInstruction(GapRule Rule, string RowKey, string? Value, bool WriteLedger);

/// <summary>
/// Destination for a patch. SqlPatchSink is the only implementation that can enrol the
/// ledger write and the target write in one transaction; tests inject a failing fake to
/// prove the rollback.
/// </summary>
public interface IPatchSink
{
    /// <summary>Applies the write and returns the value the column held beforehand.</summary>
    string? Apply(PatchInstruction instruction);
}

public sealed class SqlPatchSink : IPatchSink
{
    private readonly DbContext _db;
    private readonly string _createdBy;

    public SqlPatchSink(DbContext db, string createdBy) => (_db, _createdBy) = (db, createdBy);

    public string? Apply(PatchInstruction i)
    {
        var isIdentity = i.WriteLedger && i.Rule.ParsedKind == RuleKind.Identity;
        var sameDatabase = string.Equals(_db.TargetConnection, _db.LedgerConnection,
                                         StringComparison.OrdinalIgnoreCase);

        using var target = _db.OpenTarget();
        using var tx = target.BeginTransaction();
        try
        {
            // Read the previous value inside the transaction: without it patches.jsonl
            // cannot be replayed backwards and Revert has nothing to restore.
            var old = target.ExecuteScalar(
                $"SELECT {i.Rule.Column} FROM {i.Rule.Table} WHERE {i.Rule.Key} = @key",
                new { key = Coerce(i.RowKey) }, tx);
            var oldCanonical = old is null or DBNull ? null : Canonical.Format(old);

            if (isIdentity && sameDatabase)
            {
                WriteLedgerRow(target, tx, i);
            }
            else if (isIdentity)
            {
                // Separate ledger database: one local transaction cannot span both, so the
                // ledger row means "reserved" and applied-state comes from patches.jsonl.
                using var ledger = _db.OpenLedger();
                WriteLedgerRow(ledger, null, i);
            }

            var updated = target.Execute(
                GapQuery.Update(i.Rule, "@key", "@value"),
                new { key = Coerce(i.RowKey), value = i.Value is null ? null : Coerce(i.Value) }, tx);

            if (updated != 1)
                throw new PatchAbortedException(
                    $"Rule '{i.Rule.Id}': UPDATE for key {i.RowKey} affected {updated} rows, expected 1.");

            tx.Commit();
            return oldCanonical;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Appends the ledger row unless it is already there. A run reusing a frozen identity —
    /// after a revert, or because the column was cleared upstream — must not record it
    /// twice; the existing row is the whole reason the value came back. A <em>different</em>
    /// recorded value is a real conflict and stops the run.
    /// </summary>
    private void WriteLedgerRow(IDbConnection conn, IDbTransaction? tx, PatchInstruction i)
    {
        var existing = LedgerRepository.Existing(conn, tx, i.Rule.Table, i.RowKey, i.Rule.Column);
        if (existing is not null)
        {
            if (!string.Equals(existing, i.Value, StringComparison.Ordinal))
                throw new LedgerConflictException(
                    $"Rule '{i.Rule.Id}': the ledger records {i.Rule.Column} = '{existing}' for " +
                    $"{i.Rule.Key} {i.RowKey}, but this run would write '{i.Value}'. Ledger rows " +
                    "are never updated, so this needs a human.");
            return;
        }
        LedgerRepository.Insert(conn, tx,
            new LedgerEntry(i.Rule.Table, i.RowKey, i.Rule.Column, i.Value!, i.Rule.Id), _createdBy);
    }

    /// <summary>Values round-trip through artifacts as strings; hand the driver a number when it is one.</summary>
    private static object Coerce(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : value;
}

public sealed class Patcher
{
    private static readonly JsonSerializerOptions Compact = new(Json.Options) { WriteIndented = false };

    private readonly IPatchSink _sink;
    private readonly List<GapRule> _rules;

    public Patcher(IPatchSink sink, List<GapRule> rules) => (_sink, _rules) = (sink, rules);

    /// <summary>
    /// Independently re-verifies the plan hash against plan.approved before touching
    /// anything — it never trusts that the gate ran, so a plan edited after approval is
    /// rejected even when the scripts ran in the right order.
    /// </summary>
    public int Apply(string planPath, string approvedPath, string patchesPath)
    {
        var approval = Json.Read<Approval>(approvedPath);
        var actual = Json.Sha256File(planPath);
        if (!string.Equals(actual, approval.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PatchAbortedException(
                $"plan.json hash mismatch.\n  approved: {approval.Sha256}\n  actual:   {actual}\n" +
                "The plan changed after approval. Re-run the gate.");

        var plan = Json.Read<PlanDocument>(planPath);
        if (plan.Rules.Any(r => r.Status == "BLOCKED"))
            throw new PatchAbortedException("Plan contains a BLOCKED rule; the gate should have refused it.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(patchesPath))!);
        using var log = new StreamWriter(patchesPath, append: false);
        var applied = 0;

        foreach (var rulePlan in plan.Rules)
        {
            var rule = _rules.First(r => r.Id == rulePlan.Id);
            foreach (var patch in rulePlan.Patches)
            {
                var old = _sink.Apply(new PatchInstruction(rule, patch.Key[rule.Key], patch.Value, WriteLedger: true));
                applied++;
                log.WriteLine(JsonSerializer.Serialize(new
                {
                    ts = DateTime.UtcNow.ToString("O"),
                    rule = rule.Id,
                    id = patch.Key,
                    col = rule.Column,
                    old,
                    @new = patch.Value,
                    reason = rule.Reason,
                }, Compact));
            }
        }
        return applied;
    }
}

// ----------------------------------------------------------------- VERIFY

public sealed class Verifier
{
    private readonly DbContext _db;
    private readonly List<GapRule> _rules;

    public Verifier(DbContext db, List<GapRule> rules) => (_db, _rules) = (db, rules);

    public VerifyDocument Verify(PlanDocument plan, BaselineDocument baseline, IEnumerable<Check> consumerChecks)
    {
        // Layer 1 compares against the PLANNED row set, not the raw predicate count: a row
        // deliberately skipped is not a failure to close a gap.
        var remaining = new List<string>();
        using (var conn = _db.OpenTarget())
        {
            foreach (var rulePlan in plan.Rules)
            {
                var rule = _rules.First(r => r.Id == rulePlan.Id);
                var keys = rulePlan.Patches.Select(p => p.Key[rule.Key]).ToList();
                if (keys.Count == 0) continue;
                foreach (var stillBad in conn.Query<string>(GapQuery.RemainingAmong(rule, keys)))
                    remaining.Add($"{rule.Id}:{stillBad}");
            }
        }

        var invariants = CheckRunner.Run(_db, CheckRunner.Invariants(_rules));
        var consumer = CheckRunner.Run(_db, consumerChecks);

        var (invRegressions, invPreexisting) = Diff(baseline.Invariants, invariants);
        var (conRegressions, conPreexisting) = Diff(baseline.Consumer, consumer);

        return new VerifyDocument(plan.RunId,
            L1: remaining.Count == 0, L2: invRegressions.Count == 0, L3: conRegressions.Count == 0,
            invRegressions.Concat(conRegressions).ToList(),
            invPreexisting.Concat(conPreexisting).ToList(),
            invariants, consumer, remaining);
    }

    /// <summary>
    /// Green-to-red is a regression and fails the run. Red-to-red was already broken before
    /// anything changed — it is listed and never fatal, which is the entire reason a
    /// baseline is captured at plan time.
    /// </summary>
    private static (List<string> Regressions, List<string> Preexisting) Diff(
        List<CheckResult> before, List<CheckResult> after)
    {
        var wasPassing = before.Where(b => b.Passed).Select(b => b.Name)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var regressions = new List<string>();
        var preexisting = new List<string>();
        foreach (var r in after.Where(a => !a.Passed))
            (wasPassing.Contains(r.Name) ? regressions : preexisting).Add(r.Name);
        return (regressions, preexisting);
    }

    public static int ExitCodeFor(VerifyDocument v) =>
        !v.L1 ? ExitCodes.GapsRemain
        : !v.L2 ? ExitCodes.InvariantRegression
        : !v.L3 ? ExitCodes.ConsumerRegression
        : ExitCodes.Ok;
}
