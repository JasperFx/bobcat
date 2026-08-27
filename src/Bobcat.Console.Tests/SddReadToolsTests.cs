using System.Text.Json;
using Bobcat.Console.Contracts;
using Bobcat.Console.EventModel;
using Bobcat.Console.Mcp;
using Bobcat.Console.Runs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// The Spec Driven Development read tools (issue #167), tested the same way as the rest of
/// MonitorTools: static functions from a store + registry to JSON, no MCP transport in the
/// loop. The join under test is the designed one — spec identity {Feature}/{Scenario} from a
/// slice's Specifications (#106) against the scenario uids run evidence arrives under (#107).
/// </summary>
public class SddReadToolsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-sdd-{Guid.NewGuid():N}");
    private readonly MonitorRunRegistry _registry;
    private readonly EventModelStore _store;

    private static readonly Guid run = Guid.NewGuid();

    /// <summary>
    /// Three slices, one per coverage state: CreditWallet's spec has run (and failed),
    /// DebitWallet's spec exists but never ran, OpenWallet was never specified at all.
    /// </summary>
    private const string wallets =
        """
        {
          "name": "Wallets",
          "slices": [
            {
              "name": "CreditWallet",
              "domain": "Wallets",
              "pattern": "Command",
              "commandType": { "name": "CreditWallet", "fullName": "Wallets.CreditWallet", "assemblyName": "Wallets" },
              "emittedEvents": [{ "name": "WalletCredited", "fullName": "Wallets.WalletCredited", "assemblyName": "Wallets" }],
              "projectionTypes": [],
              "readModelTypes": [],
              "specifications": [{ "identity": "Wallet/Crediting a wallet", "resolvedTypes": [] }]
            },
            {
              "name": "DebitWallet",
              "domain": "Wallets",
              "pattern": "Command",
              "emittedEvents": [],
              "projectionTypes": [],
              "readModelTypes": [],
              "specifications": [{ "identity": "Wallet/Debiting a wallet", "resolvedTypes": [] }]
            },
            {
              "name": "OpenWallet",
              "domain": "Onboarding",
              "emittedEvents": [],
              "projectionTypes": [],
              "readModelTypes": []
            }
          ]
        }
        """;

    public SddReadToolsTests()
    {
        _registry = new MonitorRunRegistry(_dataPath);
        _store = new EventModelStore(_dataPath);

        var t0 = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        _registry.Record(
        [
            new RunStarted(run, "Wallet.Specs", "/repo", "main", "in-process", t0, 2),
            new ScenarioStarted(run, "Wallet/Crediting a wallet", "Wallet", "Crediting a wallet", 1, t0),
            new StepStarted(run, "Wallet/Crediting a wallet", "s1", "When", "CreditWallet is received"),
            new StepFinished(run, "Wallet/Crediting a wallet", "s1", "ok", 4, null),
            new StepStarted(run, "Wallet/Crediting a wallet", "s2", "Then", "the WalletLedger read model contains"),
            new StepFinished(run, "Wallet/Crediting a wallet", "s2", "failed", 9, "expected balance 100 but was 0"),
            new ScenarioFinished(run, "Wallet/Crediting a wallet", "Failed", 1, 13,
                "expected balance 100 but was 0",
                [new TouchedType("CreditWallet", "Wallets.CreditWallet", "Wallets")],
                t0.AddSeconds(5)),
            new ScenarioStarted(run, "Wallet/Opening is idempotent", "Wallet", "Opening is idempotent", 1, t0),
            new ScenarioFinished(run, "Wallet/Opening is idempotent", "CleanPass", 1, 6, null, null, t0.AddSeconds(6)),
            new RunFinished(run, 1, 1, 1, 0, 0, t0.AddMinutes(1))
        ]);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static JsonElement parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void event_model_reports_when_nothing_has_been_pushed()
    {
        parse(MonitorTools.EventModel(_store)).GetProperty("error").GetString()
            .ShouldContain("PUT /api/event-model");
    }

    [Fact]
    public void event_model_returns_the_stored_document_verbatim_when_unfiltered()
    {
        _store.TryStore(wallets).ShouldBeNull();
        MonitorTools.EventModel(_store).ShouldBe(_store.Read());
    }

    [Fact]
    public void event_model_narrows_to_one_slice_by_name()
    {
        _store.TryStore(wallets).ShouldBeNull();

        var model = parse(MonitorTools.EventModel(_store, slice: "creditwallet"));
        model.GetProperty("name").GetString().ShouldBe("Wallets");
        var slices = model.GetProperty("slices").EnumerateArray().ToArray();
        slices.Length.ShouldBe(1);
        slices[0].GetProperty("name").GetString().ShouldBe("CreditWallet");
        // Still the wire shape: enum values stay PascalCase.
        slices[0].GetProperty("pattern").GetString().ShouldBe("Command");
    }

    [Fact]
    public void event_model_narrows_to_a_domain()
    {
        _store.TryStore(wallets).ShouldBeNull();

        var slices = parse(MonitorTools.EventModel(_store, domain: "Wallets"))
            .GetProperty("slices").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString())
            .ToArray();

        slices.ShouldBe(["CreditWallet", "DebitWallet"]);
    }

    [Fact]
    public void event_model_lists_the_slices_when_a_filter_matches_nothing()
    {
        _store.TryStore(wallets).ShouldBeNull();

        var message = parse(MonitorTools.EventModel(_store, slice: "NoSuchSlice"))
            .GetProperty("error").GetString()!;
        message.ShouldContain("CreditWallet");
        message.ShouldContain("OpenWallet (domain Onboarding)");
    }

    [Fact]
    public void slice_coverage_distinguishes_the_two_gaps_from_covered()
    {
        _store.TryStore(wallets).ShouldBeNull();

        var coverage = parse(MonitorTools.SliceCoverage(_store, _registry));

        var summary = coverage.GetProperty("summary");
        summary.GetProperty("slices").GetInt32().ShouldBe(3);
        summary.GetProperty("noSpec").GetInt32().ShouldBe(1);
        summary.GetProperty("noEvidence").GetInt32().ShouldBe(1);
        summary.GetProperty("covered").GetInt32().ShouldBe(1);

        var slices = coverage.GetProperty("slices").EnumerateArray()
            .ToDictionary(s => s.GetProperty("slice").GetString()!);

        slices["OpenWallet"].GetProperty("gap").GetString().ShouldBe("no-spec");
        slices["DebitWallet"].GetProperty("gap").GetString().ShouldBe("no-evidence");

        var covered = slices["CreditWallet"];
        covered.GetProperty("gap").ValueKind.ShouldBe(JsonValueKind.Null);
        var spec = covered.GetProperty("specs")[0];
        spec.GetProperty("identity").GetString().ShouldBe("Wallet/Crediting a wallet");
        // The red spec is visible — a slice whose only spec fails is a different problem
        // from an unspecified one.
        spec.GetProperty("lastOutcome").GetString().ShouldBe("Failed");
        spec.GetProperty("lastRunId").GetGuid().ShouldBe(run);
    }

    [Fact]
    public void slice_coverage_requires_a_pushed_model()
    {
        parse(MonitorTools.SliceCoverage(_store, _registry)).GetProperty("error").GetString()
            .ShouldContain("PUT /api/event-model");
    }

    [Fact]
    public void failing_spec_defaults_to_the_first_failing_scenario_with_full_detail()
    {
        _store.TryStore(wallets).ShouldBeNull();

        var spec = parse(MonitorTools.FailingSpec(_registry, _store));

        spec.GetProperty("uid").GetString().ShouldBe("Wallet/Crediting a wallet");
        spec.GetProperty("status").GetString().ShouldBe("Failed");
        spec.GetProperty("errorMessage").GetString().ShouldBe("expected balance 100 but was 0");

        // Every step, not just the failing ones — the arrange context is part of what an
        // implementing agent reads.
        var steps = spec.GetProperty("steps").EnumerateArray().ToArray();
        steps.Length.ShouldBe(2);
        steps[0].GetProperty("name").GetString().ShouldBe("When CreditWallet is received");
        steps[0].GetProperty("status").GetString().ShouldBe("ok");
        steps[1].GetProperty("errorMessage").GetString().ShouldBe("expected balance 100 but was 0");

        // Run evidence says which types to open...
        spec.GetProperty("touchedTypes")[0].GetProperty("fullName").GetString()
            .ShouldBe("Wallets.CreditWallet");

        // ...and the Event Model join says which slice the spec belongs to.
        var slice = spec.GetProperty("slices")[0];
        slice.GetProperty("slice").GetString().ShouldBe("CreditWallet");
        slice.GetProperty("domain").GetString().ShouldBe("Wallets");
    }

    [Fact]
    public void failing_spec_returns_a_named_scenario_even_when_it_passed()
    {
        var spec = parse(MonitorTools.FailingSpec(_registry, _store, uid: "Wallet/Opening is idempotent"));

        spec.GetProperty("status").GetString().ShouldBe("CleanPass");
        // No model pushed: the join degrades to no slices rather than an error.
        spec.GetProperty("slices").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void failing_spec_says_so_when_nothing_failed()
    {
        var green = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
        _registry.Record(
        [
            new RunStarted(green, "Green", "/repo", "main", "in-process", t0, 1),
            new ScenarioStarted(green, "F/fine", "F", "fine", 1, t0),
            new ScenarioFinished(green, "F/fine", "CleanPass", 1, 3, null),
            new RunFinished(green, 0, 1, 0, 0, 0, t0.AddSeconds(10))
        ]);

        var result = parse(MonitorTools.FailingSpec(_registry, _store, runId: green.ToString()));
        result.GetProperty("message").GetString().ShouldBe("nothing failed in this run");
    }

    [Fact]
    public void failing_spec_reports_an_unknown_uid()
    {
        parse(MonitorTools.FailingSpec(_registry, _store, uid: "No/Such"))
            .GetProperty("error").GetString().ShouldContain("No/Such");
    }
}
