using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pfandwerk;

/// <summary>
/// Writes through a running Data API Builder instead of a direct SQL connection, for sites
/// whose policy requires database writes to pass through an API layer.
///
/// <para>This path is worse on every technical axis than <see cref="SqlPatchSink"/> — two
/// HTTP round trips per row instead of one local transaction — and it is not the default.
/// It exists because "all writes go through the API" is a real constraint at some sites,
/// not because it is faster or safer.</para>
///
/// <para><b>The asymmetry that cannot be engineered away.</b> A SQL INSERT into the ledger
/// database and an HTTP request to another process cannot share a transaction. On this path
/// a ledger row therefore means <em>reserved</em>, not applied; applied-state comes from
/// patches.jsonl. A failure between the two leaves a reserved identity that the next run
/// reuses and re-applies — recoverable, and a state that simply does not occur on the
/// default sink.</para>
/// </summary>
public sealed class DabPatchSink : IPatchSink
{
    /// <summary>
    /// Relaxed escaping because this JSON goes into an HTTP request body, never into HTML.
    /// The default encoder escapes '+' to \u002B, which is valid JSON but turns a real
    /// EnergyClass value like "A+" into something unreadable on the wire and in logs.
    /// </summary>
    private static readonly JsonSerializerOptions Body =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly DbContext _db;
    private readonly string _createdBy;

    /// <param name="http">Injected so tests can drive the sink without a live DAB.</param>
    /// <param name="db">Used for the ledger only. This sink never connects to the target.</param>
    public DabPatchSink(HttpClient http, DbContext db, string createdBy)
    {
        _http = http;
        _db = db;
        _createdBy = createdBy;
        if (_http.BaseAddress is null)
            throw new PatchAbortedException("DabPatchSink needs an HttpClient with a BaseAddress.");
    }

    /// <summary>
    /// One GET against the first rule's entity. Called before any write so a DAB that is not
    /// running is reported once, by URL, rather than as a confusing failure on row one.
    /// </summary>
    public void EnsureReachable(IEnumerable<GapRule> rules)
    {
        var rule = rules.FirstOrDefault();
        if (rule is null) return;

        var url = $"api/{Entity(rule)}";
        try
        {
            var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new PatchAbortedException(
                    $"DAB has no entity '{Entity(rule)}' at {_http.BaseAddress}{url}. " +
                    $"Add it to dab/dab-config.json: dab add {Entity(rule)} --source \"{rule.Table}\" " +
                    "--permissions \"anonymous:read,update\"");
        }
        catch (HttpRequestException ex)
        {
            throw new PatchAbortedException(
                $"no Data API Builder answering at {_http.BaseAddress} ({ex.Message}). " +
                "Start it with `dab start` in dab/, or use the default --sink sql.");
        }
    }

    public string? Apply(PatchInstruction i)
    {
        var entity = Entity(i.Rule);
        var url = $"api/{entity}/{i.Rule.Key}/{Uri.EscapeDataString(i.RowKey)}";

        // The previous value has to be read before the write: without it patches.jsonl
        // cannot be replayed backwards and Revert has nothing to restore. On this path that
        // costs a second round trip, and there is no way around it — DAB's PATCH response
        // reflects the new state, not the old.
        var old = ReadCurrent(url, i.Rule.Column);

        if (i.WriteLedger && i.Rule.ParsedKind == RuleKind.Identity)
        {
            // Deliberately before the PATCH, and deliberately not in a transaction with it.
            // See the class remarks: this row is a reservation until patches.jsonl says
            // otherwise.
            using var ledger = _db.OpenLedger();
            var existing = LedgerRepository.Existing(ledger, null, i.Rule.Table, i.RowKey, i.Rule.Column);
            if (existing is not null)
            {
                if (!string.Equals(existing, i.Value, StringComparison.Ordinal))
                    throw new LedgerConflictException(
                        $"Rule '{i.Rule.Id}': the ledger records {i.Rule.Column} = '{existing}' for " +
                        $"{i.Rule.Key} {i.RowKey}, but this run would write '{i.Value}'. Ledger rows " +
                        "are never updated, so this needs a human.");
            }
            else
            {
                LedgerRepository.Insert(ledger, null,
                    new LedgerEntry(i.Rule.Table, i.RowKey, i.Rule.Column, i.Value!, i.Rule.Id), _createdBy);
            }
        }

        var body = $"{{{JsonSerializer.Serialize(i.Rule.Column, Body)}:{JsonValue(i.Value)}}}";
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var result = _http.Send(request);
        if (!result.IsSuccessStatusCode)
        {
            var detail = new StreamReader(result.Content.ReadAsStream()).ReadToEnd();
            // Never fall back to SqlPatchSink. Silently taking the path a site's policy
            // forbids is worse than failing in front of someone.
            throw new PatchAbortedException(
                $"Rule '{i.Rule.Id}': PATCH {url} returned {(int)result.StatusCode} " +
                $"{result.StatusCode}. {detail.Trim()}");
        }

        return old;
    }

    private string? ReadCurrent(string url, string column)
    {
        var response = _http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(new StreamReader(response.Content.ReadAsStream()).ReadToEnd());
        // DAB wraps results as { "value": [ { ... } ] }.
        if (!doc.RootElement.TryGetProperty("value", out var rows) || rows.GetArrayLength() == 0)
            return null;
        if (!rows[0].TryGetProperty(column, out var cell) || cell.ValueKind == JsonValueKind.Null)
            return null;

        return cell.ValueKind == JsonValueKind.String ? cell.GetString() : cell.ToString();
    }

    /// <summary>
    /// DAB entities are the unqualified table name — `dbo.Security` becomes `Security`,
    /// matching how `dab add Security --source dbo.Security` was invoked.
    /// </summary>
    internal static string Entity(GapRule rule)
    {
        if (rule.Key.Contains(',')) throw new PatchAbortedException(
            $"Rule '{rule.Id}': the DAB sink supports single-column keys only. " +
            "Use --sink sql for composite keys.");

        var parts = rule.Table.Split('.');
        return parts[^1].Trim('[', ']', '"');
    }

    /// <summary>
    /// Values round-trip through plan.json as strings, but JSON is typed and DAB's
    /// `request-body-strict` is unforgiving. Numeric-looking values become JSON numbers.
    ///
    /// <para><b>Known failure mode:</b> a string column whose value happens to be all digits
    /// is sent as a number. SqlPatchSink has the same heuristic but the driver reconciles
    /// the mismatch; DAB does not. Use --sink sql for such a column, or quote the value in
    /// a way this method can see.</para>
    /// </summary>
    internal static string JsonValue(string? value)
    {
        if (value is null) return "null";
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d.ToString(CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(value, Body);
    }
}
