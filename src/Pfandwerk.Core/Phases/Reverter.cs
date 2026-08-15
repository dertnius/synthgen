using System.Text.Json;
using Pfandwerk.Core.Data;
using Pfandwerk.Core.Rules;

namespace Pfandwerk.Core.Phases;

public sealed record PatchLogEntry(string Rule, Dictionary<string, string> Id, string Column,
                                   string? Old, string? New);

/// <summary>
/// Replays patches.jsonl backwards (new -> old) through the same write path that applied
/// it. Local only, never wired into CI (hard rule 7).
///
/// <para><b>Ledger rows are never deleted.</b> That is the point: after a revert the
/// column is empty again, the ledger still records which identifier belongs to that row,
/// and the next run reuses it rather than issuing a second one. Deleting them would make
/// "frozen forever" a lie the first time anyone reverted.</para>
/// </summary>
public sealed class Reverter
{
    private readonly IPatchSink _sink;
    private readonly List<GapRule> _rules;

    public Reverter(IPatchSink sink, List<GapRule> rules) => (_sink, _rules) = (sink, rules);

    public static List<PatchLogEntry> ReadLog(string patchesPath) =>
        File.ReadLines(patchesPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                var root = doc.RootElement;
                return new PatchLogEntry(
                    root.GetProperty("rule").GetString()!,
                    root.GetProperty("id").Deserialize<Dictionary<string, string>>(Json.Options)!,
                    root.GetProperty("col").GetString()!,
                    root.TryGetProperty("old", out var o) && o.ValueKind != JsonValueKind.Null ? o.ToString() : null,
                    root.TryGetProperty("new", out var n) && n.ValueKind != JsonValueKind.Null ? n.ToString() : null);
            })
            .ToList();

    /// <summary>
    /// Reverts newest change first. <paramref name="ruleFilter"/> reverts one rule and
    /// leaves the rest applied.
    /// </summary>
    public int Revert(string patchesPath, string? ruleFilter = null)
    {
        var entries = ReadLog(patchesPath);
        entries.Reverse();

        var reverted = 0;
        foreach (var e in entries)
        {
            if (ruleFilter is not null && !string.Equals(e.Rule, ruleFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var rule = _rules.FirstOrDefault(r => r.Id == e.Rule)
                       ?? throw new GapRulesLoadException(
                           $"patches.jsonl references rule '{e.Rule}', which is not in the rules file.");

            // WriteLedger: false — restoring a column must never append to an append-only
            // ledger, and the existing row is what a later run reuses.
            _sink.Apply(new PatchInstruction(rule, e.Id[rule.Key], e.Old, WriteLedger: false));
            reverted++;
        }
        return reverted;
    }
}
