using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;

namespace Pfandwerk;

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
        if (isIdentity && !sameDatabase)
            throw new PatchAbortedException(
                "patch apply requires the target and ledger to use the same database; " +
                "separate databases cannot provide an atomic ledger reservation.");

        using var target = _db.OpenTarget();
        using var tx = target.BeginTransaction();
        try
        {
            // Read the previous value inside the transaction: without it patches.jsonl
            // cannot be replayed backwards and Revert has nothing to restore.
            var old = target.ExecuteScalar(
                $"SELECT {i.Rule.Column} FROM {i.Rule.Table} WHERE {i.Rule.Key} = @key",
                new { key = Canonical.DbValue(i.RowKey) }, tx);
            var oldCanonical = old is null or DBNull ? null : Canonical.Format(old);

            if (isIdentity && sameDatabase)
            {
                LedgerRepository.Reserve(target, tx, i.Rule, i.RowKey, i.Value, _createdBy);
            }
            var updated = target.Execute(
                GapQuery.Update(i.Rule, "@key", "@value"),
                new
                {
                    key = Canonical.DbValue(i.RowKey),
                    value = i.Value is null ? null : Canonical.DbValue(i.Value),
                }, tx);

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

        var artifactDirectory = Path.GetDirectoryName(Path.GetFullPath(patchesPath))!;
        Directory.CreateDirectory(artifactDirectory);
        var tempPath = Path.Combine(artifactDirectory,
            $".{Path.GetFileName(patchesPath)}.{Guid.NewGuid():N}.tmp");
        var applied = 0;

        try
        {
            using (var log = new StreamWriter(tempPath, append: false))
            {
                foreach (var rulePlan in plan.Rules)
                {
                    var rule = _rules.First(r => r.Id == rulePlan.Id);
                    foreach (var patch in rulePlan.Patches)
                    {
                        var old = _sink.Apply(new PatchInstruction(
                            rule, patch.Key[rule.Key], patch.Value, WriteLedger: true));
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
                log.Flush();
            }

            File.Move(tempPath, patchesPath, overwrite: true);
            return applied;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
