using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>
/// Records every request and replays canned responses, so the DAB sink's HTTP behaviour is
/// testable with no server, no SQL Server and no network.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();
    public HttpStatusCode PatchStatus { get; set; } = HttpStatusCode.NoContent;
    public string PatchBody { get; set; } = "";
    public string? GetJson { get; set; }
    public HttpStatusCode GetStatus { get; set; } = HttpStatusCode.OK;

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));

        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(GetStatus)
            { Content = new StringContent(GetJson ?? """{"value":[]}""", Encoding.UTF8, "application/json") };

        return new HttpResponseMessage(PatchStatus)
        { Content = new StringContent(PatchBody, Encoding.UTF8, "application/json") };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(Send(request, ct));
}

public sealed class DabPatchSinkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-dab").FullName;
    private readonly StubHandler _stub = new();

    private static readonly GapRule Ephemeral = new()
    {
        Id = "SEC-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
        Kind = "derived", Gap = "Bathrooms IS NULL", Fix = "security.bathroomsFromRooms",
        Inputs = new List<string> { "Rooms" }, Threshold = 500, Reason = "r",
    };

    private static readonly GapRule Identity = new()
    {
        Id = "SEC-002", Table = "dbo.Security", Key = "PropertyId", Column = "SecurityId",
        Kind = "identity", Gap = "SecurityId IS NULL", Fix = "security.securityId",
        Threshold = 50, Reason = "r",
    };

    private DbContext Db()
    {
        var path = Path.Combine(_dir, "ledger.db");
        var db = new DbContext(Provider.Sqlite, path, path);
        new LedgerRepository(db).EnsureCreated();
        return db;
    }

    private DabPatchSink Sink(DbContext? db = null) => new(
        new HttpClient(_stub) { BaseAddress = new Uri("http://localhost:5000/") },
        db ?? Db(), "test");

    // ------------------------------------------------------------ request shape

    [SqliteFact]
    public void Patches_the_entity_by_key_with_only_the_target_column()
    {
        Sink().Apply(new PatchInstruction(Ephemeral, "104", "3", WriteLedger: false));

        var patch = _stub.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("/api/Security/PropertyId/104", patch.Path);

        using var body = JsonDocument.Parse(patch.Body!);
        Assert.Equal(1, body.RootElement.EnumerateObject().Count());
        Assert.Equal(3, body.RootElement.GetProperty("Bathrooms").GetInt32());
    }

    [SqliteFact]
    public void Reads_the_previous_value_before_writing()
    {
        // Without this the run cannot be reverted: patches.jsonl would carry no old value.
        _stub.GetJson = """{"value":[{"PropertyId":104,"Bathrooms":7}]}""";

        var old = Sink().Apply(new PatchInstruction(Ephemeral, "104", "3", WriteLedger: false));

        Assert.Equal("7", old);
        Assert.Equal(HttpMethod.Get, _stub.Requests[0].Method);
        Assert.Equal(HttpMethod.Patch, _stub.Requests[1].Method);
    }

    [SqliteFact]
    public void A_null_column_reads_back_as_null_not_as_the_string_null()
    {
        _stub.GetJson = """{"value":[{"PropertyId":104,"Bathrooms":null}]}""";
        Assert.Null(Sink().Apply(new PatchInstruction(Ephemeral, "104", "3", WriteLedger: false)));
    }

    // ------------------------------------------------------------------ failure

    [SqliteFact]
    public void A_rejected_patch_aborts_and_never_falls_back_to_sql()
    {
        // Silently taking the path a site's policy forbids is worse than failing loudly.
        _stub.PatchStatus = HttpStatusCode.BadRequest;
        _stub.PatchBody = """{"error":{"message":"Invalid value"}}""";

        var ex = Assert.Throws<PatchAbortedException>(
            () => Sink().Apply(new PatchInstruction(Ephemeral, "104", "3", WriteLedger: false)));

        Assert.Contains("400", ex.Message);
        Assert.Contains("Invalid value", ex.Message);
    }

    [Fact]
    public void An_unreachable_dab_is_reported_once_by_url()
    {
        var dead = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost:9/") };
        var sink = new DabPatchSink(dead, new DbContext(Provider.Sqlite, "x.db", "x.db"), "test");

        var ex = Assert.Throws<PatchAbortedException>(() => sink.EnsureReachable(new[] { Ephemeral }));
        Assert.Contains("localhost:9", ex.Message);
        Assert.Contains("dab start", ex.Message);
    }

    [Fact]
    public void A_missing_entity_names_the_command_that_adds_it()
    {
        _stub.GetStatus = HttpStatusCode.NotFound;
        var sink = new DabPatchSink(
            new HttpClient(_stub) { BaseAddress = new Uri("http://localhost:5000/") },
            new DbContext(Provider.Sqlite, "x.db", "x.db"), "test");

        var ex = Assert.Throws<PatchAbortedException>(() => sink.EnsureReachable(new[] { Ephemeral }));
        Assert.Contains("dab add Security --source \"dbo.Security\"", ex.Message);
    }

    // ------------------------------------------------------------------- ledger

    [SqliteFact]
    public void An_identity_reserves_the_ledger_row_before_the_patch()
    {
        var db = Db();
        Sink(db).Apply(new PatchInstruction(Identity, "102", "DE000ABC", WriteLedger: true));

        using var conn = db.OpenLedger();
        Assert.Equal("DE000ABC", conn.ExecuteScalar<string>(
            "SELECT Value FROM dbo.SyntheticLedger WHERE RowKey = '102'"));
        Assert.Equal(HttpMethod.Patch, _stub.Requests.Last().Method);
    }

    [SqliteFact]
    public void A_reused_identity_is_not_recorded_twice()
    {
        var db = Db();
        var instruction = new PatchInstruction(Identity, "102", "DE000ABC", WriteLedger: true);
        Sink(db).Apply(instruction);
        Sink(db).Apply(instruction);   // e.g. after a revert

        using var conn = db.OpenLedger();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SyntheticLedger"));
    }

    [SqliteFact]
    public void A_conflicting_identity_throws_before_any_http_call()
    {
        var db = Db();
        Sink(db).Apply(new PatchInstruction(Identity, "102", "DE000ABC", WriteLedger: true));
        var httpSoFar = _stub.Requests.Count;

        Assert.Throws<LedgerConflictException>(() =>
            Sink(db).Apply(new PatchInstruction(Identity, "102", "DE000XYZ", WriteLedger: true)));

        // Only the GET that reads the previous value; no PATCH was attempted.
        Assert.DoesNotContain(_stub.Requests.Skip(httpSoFar), r => r.Method == HttpMethod.Patch);
    }

    // -------------------------------------------------------------- JSON typing

    [Theory]
    [InlineData("3", "3")]
    [InlineData("-7", "-7")]
    [InlineData("2.5", "2.5")]
    [InlineData("DE000ABC", "\"DE000ABC\"")]
    [InlineData("A+", "\"A+\"")]
    [InlineData(null, "null")]
    public void Numeric_values_serialise_as_numbers_and_the_rest_as_strings(string? value, string expected) =>
        Assert.Equal(expected, Canonical.JsonValue(value));

    [Fact]
    public void Composite_keys_are_refused_rather_than_half_supported()
    {
        var composite = new GapRule
        {
            Id = "X-001", Table = "dbo.Security", Key = "PropertyId,Version", Column = "Bathrooms",
            Kind = "ephemeral", Gap = "1=1", Fix = "property.energyClass", Threshold = 1, Reason = "r",
        };
        var ex = Assert.Throws<PatchAbortedException>(() => DabPatchSink.Entity(composite));
        Assert.Contains("single-column keys only", ex.Message);
    }

    [Fact]
    public void The_entity_is_the_table_without_its_schema() =>
        Assert.Equal("Security", DabPatchSink.Entity(Ephemeral));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
