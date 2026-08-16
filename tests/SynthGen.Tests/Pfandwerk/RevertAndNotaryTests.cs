using Pfandwerk;

namespace Pfandwerk.Tests;

/// <summary>Records every instruction and can be told to fail, so ordering and rollback are testable.</summary>
internal sealed class FakePatchSink : IPatchSink
{
    public List<PatchInstruction> Applied { get; } = new();
    public Func<PatchInstruction, bool>? FailWhen { get; set; }

    public string? Apply(PatchInstruction i)
    {
        if (FailWhen?.Invoke(i) == true) throw new PatchAbortedException("injected failure");
        Applied.Add(i);
        return "previous";
    }
}

public class ReverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-revert").FullName;

    private static readonly List<GapRule> Rules = new()
    {
        new() { Id = "SEC-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
                Kind = "derived", Gap = "Bathrooms IS NULL", Fix = "security.bathroomsFromRooms",
                Inputs = new List<string> { "Rooms" }, Threshold = 500, Reason = "r" },
        new() { Id = "SEC-002", Table = "dbo.Security", Key = "PropertyId", Column = "SecurityId",
                Kind = "identity", Gap = "SecurityId IS NULL", Fix = "security.securityId",
                Threshold = 50, Reason = "r" },
    };

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(_dir, "patches.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string Line(string rule, string key, string col, string? old, string @new) =>
        $$"""{"ts":"2026-08-15T00:00:00Z","rule":"{{rule}}","id":{"PropertyId":"{{key}}"},"col":"{{col}}","old":{{(old is null ? "null" : $"\"{old}\"")}},"new":"{{@new}}","reason":"r"}""";

    [Fact]
    public void Reverts_newest_first()
    {
        var log = WriteLog(
            Line("SEC-001", "101", "Bathrooms", null, "1"),
            Line("SEC-002", "102", "SecurityId", null, "DE000AAA"));

        var sink = new FakePatchSink();
        var count = new Reverter(sink, Rules).Revert(log);

        Assert.Equal(2, count);
        Assert.Equal("SEC-002", sink.Applied[0].Rule.Id);   // newest first
        Assert.Equal("SEC-001", sink.Applied[1].Rule.Id);
    }

    [Fact]
    public void Restores_the_previous_value_including_null()
    {
        var log = WriteLog(Line("SEC-001", "101", "Bathrooms", null, "1"));
        var sink = new FakePatchSink();
        new Reverter(sink, Rules).Revert(log);

        Assert.Null(sink.Applied[0].Value);
    }

    [Fact]
    public void Never_writes_to_the_ledger()
    {
        // Restoring a column must not append to an append-only ledger: the existing row is
        // exactly what makes a later run reuse the same identifier.
        var log = WriteLog(Line("SEC-002", "102", "SecurityId", null, "DE000AAA"));
        var sink = new FakePatchSink();
        new Reverter(sink, Rules).Revert(log);

        Assert.False(sink.Applied[0].WriteLedger);
    }

    [Fact]
    public void Can_revert_a_single_rule()
    {
        var log = WriteLog(
            Line("SEC-001", "101", "Bathrooms", null, "1"),
            Line("SEC-002", "102", "SecurityId", null, "DE000AAA"));

        var sink = new FakePatchSink();
        var count = new Reverter(sink, Rules).Revert(log, ruleFilter: "SEC-002");

        Assert.Equal(1, count);
        Assert.Equal("SEC-002", Assert.Single(sink.Applied).Rule.Id);
    }

    [Fact]
    public void Rejects_a_log_naming_an_unknown_rule()
    {
        var log = WriteLog(Line("GONE-001", "101", "Bathrooms", null, "1"));
        var ex = Assert.Throws<RulesLoadException>(() => new Reverter(new FakePatchSink(), Rules).Revert(log));
        Assert.Contains("GONE-001", ex.Message);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public class PatcherGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pfandwerk-patch").FullName;

    private static PlanDocument Plan() => new("run-1", new string('b', 64), new List<RulePlan>
    {
        new("SEC-001", "dbo.Security", "Bathrooms", "derived", "OK", 1, 500, "r",
            new List<PlannedPatch> { new(new Dictionary<string, string> { ["PropertyId"] = "101" }, "1", null) },
            new List<SkippedRow>(), new List<NewIdentity>()),
    });

    private static readonly List<GapRule> Rules = new()
    {
        new() { Id = "SEC-001", Table = "dbo.Security", Key = "PropertyId", Column = "Bathrooms",
                Kind = "derived", Gap = "Bathrooms IS NULL", Fix = "security.bathroomsFromRooms",
                Inputs = new List<string> { "Rooms" }, Threshold = 500, Reason = "r" },
    };

    private (string plan, string approved, string patches) Artifacts(string sha)
    {
        var plan = Path.Combine(_dir, "plan.json");
        var approved = Path.Combine(_dir, "plan.approved");
        Json.Write(plan, Plan());
        Json.Write(approved, new Approval(sha ?? Json.Sha256File(plan), "u", "u@example.test", "2026-08-15T00:00:00Z"));
        return (plan, approved, Path.Combine(_dir, "patches.jsonl"));
    }

    [Fact]
    public void Applies_when_the_hash_matches()
    {
        var planPath = Path.Combine(_dir, "plan.json");
        Json.Write(planPath, Plan());
        var (plan, approved, patches) = Artifacts(Json.Sha256File(planPath));

        var applied = new Patcher(new FakePatchSink(), Rules).Apply(plan, approved, patches);
        Assert.Equal(1, applied);
    }

    [Fact]
    public void Aborts_when_the_plan_changed_after_approval()
    {
        // The patcher never trusts that the gate ran first: a plan edited afterwards is
        // rejected even when the scripts ran in the right order.
        var (plan, approved, patches) = Artifacts(new string('0', 64));

        var ex = Assert.Throws<PatchAbortedException>(
            () => new Patcher(new FakePatchSink(), Rules).Apply(plan, approved, patches));
        Assert.Contains("hash mismatch", ex.Message);
    }

    [Fact]
    public void Records_the_previous_value_so_the_run_can_be_reverted()
    {
        var planPath = Path.Combine(_dir, "plan.json");
        Json.Write(planPath, Plan());
        var (plan, approved, patches) = Artifacts(Json.Sha256File(planPath));

        new Patcher(new FakePatchSink(), Rules).Apply(plan, approved, patches);

        var entry = Assert.Single(Reverter.ReadLog(patches));
        Assert.Equal("previous", entry.Old);
        Assert.Equal("1", entry.New);
    }

    [Fact]
    public void Does_not_publish_a_partial_log_when_a_later_patch_fails()
    {
        var planPath = Path.Combine(_dir, "plan.json");
        var approvedPath = Path.Combine(_dir, "plan.approved");
        var patchesPath = Path.Combine(_dir, "patches.jsonl");
        var plan = Plan() with
        {
            Rules = new List<RulePlan>
            {
                Plan().Rules[0] with
                {
                    Patches = new List<PlannedPatch>
                    {
                        new(new Dictionary<string, string> { ["PropertyId"] = "101" }, "1", null),
                        new(new Dictionary<string, string> { ["PropertyId"] = "102" }, "2", null),
                    },
                },
            },
        };
        Json.Write(planPath, plan);
        Json.Write(approvedPath,
            new Approval(Json.Sha256File(planPath), "u", "u@example.test", "2026-08-15T00:00:00Z"));
        var sink = new FakePatchSink
        {
            FailWhen = i => i.RowKey == "102",
        };

        Assert.Throws<PatchAbortedException>(() =>
            new Patcher(sink, Rules).Apply(planPath, approvedPath, patchesPath));

        Assert.False(File.Exists(patchesPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), path =>
            Path.GetFileName(path).StartsWith(".patches.jsonl.", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_identity_apply_with_a_separate_ledger_database()
    {
        var identity = new GapRule
        {
            Id = "SEC-002", Table = "dbo.Security", Key = "PropertyId", Column = "SecurityId",
            Kind = "identity", Gap = "SecurityId IS NULL", Fix = "security.securityId",
            Threshold = 50, Reason = "r",
        };
        var db = new DbContext(Provider.Sqlite,
            Path.Combine(_dir, "target.db"), Path.Combine(_dir, "ledger.db"));

        var ex = Assert.Throws<PatchAbortedException>(() =>
            new SqlPatchSink(db, "test").Apply(
                new PatchInstruction(identity, "101", "DE000AAA", WriteLedger: true)));

        Assert.Contains("same database", ex.Message);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
