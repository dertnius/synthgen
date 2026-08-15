using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Rules;

namespace Pfandwerk.Core.Phases;

public sealed class PatchAbortedException : Exception
{
    public PatchAbortedException(string message) : base(message) { }
}

public sealed record PatchInstruction(GapRule Rule, string RowKey, string Value, string? Old);

/// <summary>
/// Destination for a patch. SqlPatchSink is the default and the only one that can enrol
/// the ledger write and the target write in a single transaction; DabPatchSink issues a
/// REST PATCH and cannot, which is why a ledger row on that path means "reserved" rather
/// than "applied". Tests inject a failing fake to prove the rollback.
/// </summary>
public interface IPatchSink
{
    void Apply(PatchInstruction instruction);
}

public sealed class SqlPatchSink : IPatchSink
{
    private readonly DbContext _db;
    private readonly string _createdBy;

    public SqlPatchSink(DbContext db, string createdBy) => (_db, _createdBy) = (db, createdBy);

    public void Apply(PatchInstruction i)
    {
        var isIdentity = i.Rule.ParsedKind == RuleKind.Identity;
        var sameDatabase = string.Equals(_db.TargetConnection, _db.LedgerConnection,
                                         StringComparison.OrdinalIgnoreCase);

        using var target = _db.OpenTarget();
        using var tx = target.BeginTransaction();
        try
        {
            if (isIdentity && sameDatabase)
            {
                LedgerRepository.Insert(target, tx,
                    new LedgerEntry(i.Rule.Table, i.RowKey, i.Rule.Column, i.Value, i.Rule.Id), _createdBy);
            }
            else if (isIdentity)
            {
                // Separate ledger database: a single local transaction cannot span both, so
                // the ledger row is written first and means "reserved". Applied-state comes
                // from patches.jsonl.
                using var ledger = _db.OpenLedger();
                LedgerRepository.Insert(ledger, null,
                    new LedgerEntry(i.Rule.Table, i.RowKey, i.Rule.Column, i.Value, i.Rule.Id), _createdBy);
            }

            var updated = target.Execute(
                GapQuery.Update(i.Rule, "@key", "@value"),
                new { key = Coerce(i.RowKey), value = Coerce(i.Value) }, tx);

            if (updated != 1)
                throw new PatchAbortedException(
                    $"Rule '{i.Rule.Id}': UPDATE for key {i.RowKey} affected {updated} rows, expected 1.");

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Values round-trip through artifacts as strings; give the driver a number when it is one.</summary>
    private static object Coerce(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : value;
}

public sealed class Patcher
{
    private readonly IPatchSink _sink;
    private readonly List<GapRule> _rules;

    public Patcher(IPatchSink sink, List<GapRule> rules) => (_sink, _rules) = (sink, rules);

    /// <summary>
    /// Independently re-verifies the plan hash against plan.approved before touching
    /// anything — it never trusts that the gate ran first, so a plan edited after approval
    /// is rejected even when the scripts ran in the right order.
    /// </summary>
    public int Apply(string planPath, string approvedPath, string patchesPath, bool stopOnRowError = true)
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
                var rowKey = patch.Key[rule.Key];
                try
                {
                    _sink.Apply(new PatchInstruction(rule, rowKey, patch.Value, null));
                    applied++;
                    log.WriteLine(JsonSerializer.Serialize(new
                    {
                        ts = DateTime.UtcNow.ToString("O"),
                        rule = rule.Id,
                        id = patch.Key,
                        col = rule.Column,
                        old = (string?)null,
                        @new = patch.Value,
                        reason = rule.Reason,
                    }, Json.Options.WriteIndented ? new JsonSerializerOptions(Json.Options) { WriteIndented = false } : Json.Options));
                }
                catch (Exception ex) when (!stopOnRowError)
                {
                    Console.Error.WriteLine($"warn: {rule.Id} key {rowKey}: {ex.Message}");
                }
            }
        }

        return applied;
    }
}
