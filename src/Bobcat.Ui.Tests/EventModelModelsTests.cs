using System.Text.Json;
using Bobcat.Ui.Contracts;
using Bobcat.Supervisor.Tests;
using Bobcat.Ui.EventModel;
using Bobcat.Ui.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// bobcat#799 — several models on one console. The walkthrough's canvas opened on a model pushed a
/// fortnight earlier from another repository while every run on the board was of a different one, with
/// no way to reach the other and nothing saying it was stale. Each contribution is now kept per model
/// AND source, with when it was pushed and from where, and the console lists them.
/// </summary>
public class EventModelModelsTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-799-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private static string model(string name, string slice = "CreditWallet")
        => $$"""{ "name": "{{name}}", "slices": [{ "name": "{{slice}}", "projectionTypes": [], "readModelTypes": [] }] }""";

    private static string nameOf(string? json) => JsonDocument.Parse(json!).RootElement.GetProperty("name").GetString()!;

    [Fact]
    public void two_applications_pushing_the_default_source_are_two_models_not_one_replacing_the_other()
    {
        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("CritterCrush")).ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(1));
        store.TryStore(model("BankAccount")).ShouldBeNull();

        // The bare read keeps its meaning: the model pushed last.
        nameOf(store.Read()).ShouldBe("BankAccount");

        // ...and the other one is still there, by name.
        nameOf(store.Read("CritterCrush")).ShouldBe("CritterCrush");
        store.Read("Nope").ShouldBeNull();
        store.Models().Select(m => m.Name).ShouldBe(["BankAccount", "CritterCrush"]);
    }

    [Fact]
    public void each_source_records_when_it_was_pushed_and_from_which_checkout()
    {
        var pushedAt = _time.GetUtcNow();
        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("BankAccount"), EventModelStore.DefaultSource,
            new EventModelPushOrigin("/code/wolverine", "JasperFx/wolverine", "main")).ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(5));
        store.TryStore(model("BankAccount", "FreezeAccount"), "BankAccountES-Tests").ShouldBeNull();

        var summary = store.Models().ShouldHaveSingleItem();
        summary.Name.ShouldBe("BankAccount");
        summary.LastPushedAt.ShouldBe(pushedAt.AddMinutes(5));
        summary.Slices.ShouldBe(2);

        // Newest first, and a push that said nothing about where it came from says nothing here either.
        summary.Sources.Select(s => s.Source).ShouldBe(["BankAccountES-Tests", "default"]);
        summary.Sources[1].ShouldBe(new EventModelSourceSummary(
            "default", pushedAt, false, "/code/wolverine", "JasperFx/wolverine", "main"));
        summary.Sources[0].Repository.ShouldBeNull();
        summary.Sources[0].Root.ShouldBeNull();
    }

    [Fact]
    public void push_times_and_provenance_survive_a_restart()
    {
        var pushedAt = _time.GetUtcNow();
        new EventModelStore(_dataPath, _time).TryStore(model("BankAccount"), "default",
            new EventModelPushOrigin("/code/wolverine", "JasperFx/wolverine", null)).ShouldBeNull();

        var source = new EventModelStore(_dataPath).Models().ShouldHaveSingleItem().Sources.ShouldHaveSingleItem();
        source.PushedAt.ShouldBe(pushedAt);
        source.PushedAtIsFileTime.ShouldBeFalse();
        source.Repository.ShouldBe("JasperFx/wolverine");
        source.Branch.ShouldBeNull();
    }

    /// <summary>
    /// The restart used to take the current name from whichever file the directory listing returned
    /// first. It is the newest push now, which is what it meant while the console was up.
    /// </summary>
    [Fact]
    public void after_a_restart_the_bare_read_serves_the_model_pushed_last()
    {
        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("Zebra")).ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(1));
        store.TryStore(model("Aardvark"), "specs").ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(1));
        store.TryStore(model("Mongoose"), "other").ShouldBeNull();

        var restarted = new EventModelStore(_dataPath, _time);
        restarted.Name.ShouldBe("Mongoose");
        nameOf(restarted.Read()).ShouldBe("Mongoose");
    }

    [Fact]
    public void files_from_before_799_are_moved_into_their_models_with_the_file_time_as_the_push_time()
    {
        Directory.CreateDirectory(_dataPath);
        var legacy = Path.Combine(_dataPath, "event-model.CritterCrush-Specs.json");
        File.WriteAllText(legacy, model("CritterCrush"));
        var written = new DateTime(2026, 10, 13, 8, 52, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(legacy, written);

        var store = new EventModelStore(_dataPath, _time);

        var source = store.Models().ShouldHaveSingleItem().Sources.ShouldHaveSingleItem();
        source.Source.ShouldBe("CritterCrush-Specs");
        source.PushedAt.ShouldBe(new DateTimeOffset(written));
        source.PushedAtIsFileTime.ShouldBeTrue();
        store.Name.ShouldBe("CritterCrush");

        // Renamed rather than deleted, so an upgrade loses nothing that was on disk, and the moved copy
        // serves from here on.
        File.Exists(legacy).ShouldBeFalse();
        File.Exists(legacy + EventModelStore.MigratedSuffix).ShouldBeTrue();
        nameOf(new EventModelStore(_dataPath, _time).Read("CritterCrush")).ShouldBe("CritterCrush");
    }

    [Fact]
    public void two_names_that_make_the_same_file_name_are_still_two_models()
    {
        EventModelStore.FolderNameFor("Bank.Account").ShouldNotBe(EventModelStore.FolderNameFor("Bank-Account"));

        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("Bank.Account", "A")).ShouldBeNull();
        store.TryStore(model("Bank-Account", "B")).ShouldBeNull();

        var restarted = new EventModelStore(_dataPath, _time);
        restarted.Read("Bank.Account")!.ShouldContain("\"A\"");
        restarted.Read("Bank-Account")!.ShouldContain("\"B\"");
    }

    [Fact]
    public void the_endpoint_reads_a_named_model_and_404s_an_unknown_one()
    {
        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("CritterCrush")).ShouldBeNull();
        store.TryStore(model("BankAccount")).ShouldBeNull();

        EventModelEndpoints.Get(store, "CritterCrush").ShouldBeOfType<ContentHttpResult>()
            .ResponseContent!.ShouldContain("CritterCrush");
        EventModelEndpoints.Get(store).ShouldBeOfType<ContentHttpResult>()
            .ResponseContent!.ShouldContain("BankAccount");
        EventModelEndpoints.Get(store, "Nope").ShouldBeOfType<NotFound>();
    }

    // ---- the canvas's default choice ---------------------------------------------------------------

    private static EventModelSummary summary(string name, int minutesAgo, string? repository = null, string? root = null)
    {
        var at = new DateTimeOffset(2026, 10, 26, 16, 0, 0, TimeSpan.Zero).AddMinutes(-minutesAgo);
        return new EventModelSummary(name, at, 1, [new EventModelSourceSummary("default", at, false, root, repository, null)]);
    }

    /// <summary>
    /// The walkthrough exactly: CritterCrush was pushed last (by a spec run of another repository), while
    /// the board held only BankAccountES runs, whose model came from JasperFx/wolverine.
    /// </summary>
    [Fact]
    public void the_canvas_prefers_a_model_from_a_repository_with_runs_on_the_board_over_the_newest()
    {
        var listing = EventModelListing.Build(
            [summary("CritterCrush", 0, "JasperFx/CritterCrush"), summary("BankAccount", 30, "JasperFx/wolverine")],
            current: "CritterCrush",
            [("/code/wolverine", "JasperFx/wolverine")]);

        listing.Current.ShouldBe("CritterCrush");
        listing.Preferred.ShouldBe("BankAccount");
        listing.Models.Single(m => m.Name == "BankAccount").HasRunsOnBoard.ShouldBeTrue();
        listing.Models.Single(m => m.Name == "CritterCrush").HasRunsOnBoard.ShouldBeFalse();
    }

    [Fact]
    public void a_checkout_with_no_resolvable_remote_matches_on_its_root()
    {
        var listing = EventModelListing.Build(
            [summary("Newer", 0), summary("Mine", 30, root: "/scratch/app")],
            current: "Newer",
            [("/scratch/app", null)]);

        listing.Preferred.ShouldBe("Mine");
    }

    [Fact]
    public void with_no_model_from_a_repository_on_the_board_the_canvas_takes_the_newest()
    {
        var listing = EventModelListing.Build(
            [summary("Older", 30, "JasperFx/a"), summary("Newer", 0)],
            current: "Newer",
            [("/code/elsewhere", "JasperFx/elsewhere")]);

        listing.Preferred.ShouldBe("Newer");
        EventModelListing.Build([], null, []).Preferred.ShouldBeNull();
    }

    [Fact]
    public void the_listing_endpoint_joins_the_store_to_the_runs_on_the_board()
    {
        var store = new EventModelStore(_dataPath, _time);
        store.TryStore(model("BankAccount"), "default", new EventModelPushOrigin("/scratch/wolverine", null, null))
            .ShouldBeNull();
        _time.Advance(TimeSpan.FromMinutes(1));
        store.TryStore(model("CritterCrush"), "CritterCrush-Specs").ShouldBeNull();

        using var registry = new MonitorRunRegistry(Path.Combine(_dataPath, "runs"));
        registry.Record([new RunStarted(Guid.NewGuid(), "BankAccountES.Tests", "/scratch/wolverine", "main",
            "in-process", DateTimeOffset.UtcNow, 1)]);

        var listing = EventModelEndpoints.List(store, registry);

        listing.Current.ShouldBe("CritterCrush");
        listing.Preferred.ShouldBe("BankAccount");
        listing.Models.Select(m => m.Name).ShouldBe(["CritterCrush", "BankAccount"]);
    }
}
