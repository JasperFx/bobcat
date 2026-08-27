using Bobcat.Console.EventModel;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #171 — everything `bobcat watch-event-model` decides before it starts a process. The
/// command itself is process orchestration and is not meaningfully unit-testable; this is the
/// decidable-from-input half, extracted so it can be (the CritterWatch `cw-*` precedent).
/// </summary>
public class EventModelWatchPlanTests
{
    private static EventModelWatchPlan Plan(string project = "../MyApp", string? url = null, bool up = true)
        => EventModelWatchPlan.For(project, url, up);

    [Fact]
    public void the_exporter_is_pointed_at_the_api_endpoint_not_the_base_url()
    {
        // ⚠️ THE load-bearing assertion of this type. Wolverine's `event-model --url` PUTs to the URL
        // VERBATIM, so the base-URL form shown in its own XML comment, in this issue, and in the
        // design note answers 404. Verified against a running console: base URL 404, endpoint 204.
        Plan(url: "http://localhost:5525").WatcherArguments
            .ShouldContain("http://localhost:5525/api/event-model");

        Plan(url: "http://localhost:5525").WatcherArguments
            .ShouldNotContain("http://localhost:5525");
    }

    [Fact]
    public void the_watcher_spawns_dotnet_watch_around_the_export()
    {
        // ⚠️ Never an internal loop: a watch inside one process re-serializes the assembly it
        // already loaded and would never show an edit (D1). Only a fresh process picks up
        // recompiled handlers, so `dotnet watch run` is the mechanism, not a convenience.
        Plan(project: "../MyApp").WatcherArguments.ShouldBe([
            "watch", "run",
            "--project", "../MyApp",
            "--",
            "event-model",
            "--url", "http://localhost:5525/api/event-model",
        ]);
    }

    [Fact]
    public void the_arguments_stay_a_list_so_a_path_with_a_space_survives()
    {
        // The normal case on macOS and Windows. Flattening these into one command line is exactly
        // how such a path breaks, so the shape is part of the contract.
        var plan = Plan(project: "/Users/dev/My Projects/MyApp");

        plan.WatcherArguments.ShouldContain("/Users/dev/My Projects/MyApp");
    }

    [Fact]
    public void the_default_console_is_the_one_launchSettings_uses()
    {
        Plan(url: null).ConsoleUrl.ShouldBe("http://localhost:5525");
        Plan(url: "   ").ConsoleUrl.ShouldBe("http://localhost:5525");
    }

    [Fact]
    public void a_trailing_slash_never_doubles_up()
    {
        var plan = Plan(url: "http://localhost:9000/");

        plan.ConsoleUrl.ShouldBe("http://localhost:9000");
        plan.EventModelApiUrl.ShouldBe("http://localhost:9000/api/event-model");
        plan.EventModelPageUrl.ShouldBe("http://localhost:9000/event-model");
    }

    [Fact]
    public void a_console_is_started_only_when_nothing_answered()
    {
        Plan(up: true).StartConsole.ShouldBeFalse();
        Plan(up: false).StartConsole.ShouldBeTrue();
    }

    [Fact]
    public void the_page_url_is_the_one_a_human_opens()
    {
        // Distinct from the API endpoint the exporter PUTs to — the command prints this one.
        Plan().EventModelPageUrl.ShouldBe("http://localhost:5525/event-model");
    }
}
