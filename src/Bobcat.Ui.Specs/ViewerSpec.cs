using System.Text.Json;
using System.Xml.Linq;
using Alba;
using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Ui.Contracts;
using Bobcat.Ui.Repositories;
using JasperFx.Core;
using Wolverine.Tracking;

namespace Bobcat.Ui.Specs;

/// <summary>
/// The whole vocabulary of the viewer suite, as the base every feature class derives from. Givens
/// ingest events over <c>POST /api/ingest</c> exactly as a publisher would; Whens hit the eject,
/// restart and export seams; Thens read the public wire back -- <c>GET /api/runs</c>,
/// <c>GET /api/runs/{id}</c>, and the exports.
/// </summary>
/// <remarks>
/// <para>
/// This was <c>ViewerSteps</c>, a grammar module the Gherkin features shared with
/// <c>[IncludeGrammars]</c>. Bobcat 0.50.0 keeps only the projected lane, and the step attributes work
/// the same way there: a test calls the method, the generated interceptor renders the sentence. So the
/// vocabulary barely moved. What did move is every Then that used to RETURN its value for the Gherkin
/// runner to compare against the step text: a C# caller has no step text, so each one takes the expected
/// value as an argument and reports the comparison itself, through <see cref="SpecAssert"/>.
/// </para>
/// <para>
/// xUnit makes a fresh instance per test, which is what makes the fields below per-test state -- the job
/// "a fresh fixture per scenario" did before.
/// </para>
/// <para>
/// Every helper is <c>internal</c>, never <c>protected</c>: the interceptor that renders a call is an
/// extension method, and an extension method cannot see a protected member.
/// </para>
/// </remarks>
public abstract class ViewerSpec(MonitorHost host) : IAsyncLifetime
{
    private readonly Dictionary<string, Guid> _runs = new();
    private readonly Dictionary<string, string> _outcomes = new();
    private Guid _current;
    private string? _currentUid;
    private int _stepCounter;
    // Issue #169 -- the EventModelChanged messages the last event-model push broadcast.
    private string[] _broadcastModelNames = [];

    private int _lastStatus;
    private string? _lastContentType;
    private string _lastBody = "";

    private Guid current => _current == Guid.Empty
        ? throw new InvalidOperationException("No run has been started in this test yet.")
        : _current;

    public virtual ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------------------------------------------------------------- ingestion (Given)

    [Given("a run \"{suite}\" has started")]
    internal Task GivenARunHasStarted(string suite) => startRun(suite, totalScenarios: null, tag: null);

    [Given("a run \"{suite}\" has started with {total} scenarios")]
    internal Task GivenARunHasStartedWith(string suite, int total) => startRun(suite, total, tag: null);

    [Given("a run \"{suite}\" tagged \"{tag}\" has started")]
    internal Task GivenATaggedRunHasStarted(string suite, string tag) => startRun(suite, totalScenarios: null, tag);

    /// <summary>
    /// A run with a start in the past, so a spec can say which of several is older without
    /// sleeping. The stamp is the publisher's, which is exactly what "older" means to the
    /// bulk-eject filter (issue #197) and to the board's ordering (issue #196).
    /// </summary>
    [Given("a run \"{suite}\" started {minutes} minutes ago")]
    internal Task GivenARunStartedMinutesAgo(string suite, int minutes)
        => startRun(suite, totalScenarios: null, tag: null, startedAt: DateTimeOffset.UtcNow.AddMinutes(-minutes));

    /// <summary>A run from a real checkout -- this one -- so its repository resolves to a slug (stoat#83).
    /// Every other run here comes from <c>/repo/bobcat</c>, which is no checkout, and is unattributed.</summary>
    [Given("a run \"{suite}\" in this checkout has started")]
    internal Task GivenARunInThisCheckoutHasStarted(string suite)
        => startRun(suite, totalScenarios: null, tag: null, repository: thisCheckout());

    private static string thisCheckout() => GitRemotes.FindRoot(AppContext.BaseDirectory)!;

    private async Task startRun(string suite, int? totalScenarios, string? tag, DateTimeOffset? startedAt = null,
        string repository = "/repo/bobcat")
    {
        var runId = Guid.NewGuid();
        _runs[suite] = runId;
        _current = runId;
        _outcomes.Clear();

        await ingest(new RunStarted(
            runId, suite, repository, "main", "in-process",
            startedAt ?? DateTimeOffset.UtcNow, totalScenarios, tag));
    }

    // Issue #195: what a supervised worker that is not a Bobcat runner produces. It publishes
    // no scenarios of its own -- the supervisor forwards its live per-test stream instead -- so
    // these two are the whole vocabulary such a run has.

    [Given("a foreign test \"{uid}\" has started")]
    internal Task GivenAForeignTestHasStarted(string uid)
        => ingest(new TestStarted(current, uid, uid, 0, DateTimeOffset.UtcNow));

    [Given("a foreign test \"{uid}\" finished as \"{state}\"")]
    internal Task GivenAForeignTestFinished(string uid, string state)
        => ingest(new TestFinished(current, uid, uid, state, 12, 0, DateTimeOffset.UtcNow));

    [Given("the scenario \"{uid}\" has started")]
    internal Task GivenTheScenarioHasStarted(string uid) => scenarioStarted(uid, attempt: 1);

    [Given("the scenario \"{uid}\" started its attempt {attempt}")]
    internal Task GivenTheScenarioStartedItsAttempt(string uid, int attempt) => scenarioStarted(uid, attempt);

    private Task scenarioStarted(string uid, int attempt)
    {
        _currentUid = uid;
        var (feature, scenario) = split(uid);
        return ingest(new ScenarioStarted(current, uid, feature, scenario, attempt, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Steps for the scenario most recently started, one row per step:
    /// <c>| Kind | Text | Status |</c>.
    /// </summary>
    [Given("these steps ran")]
    internal async Task GivenTheseStepsRan(StepTable steps)
    {
        var uid = _currentUid ?? throw new InvalidOperationException("No scenario has started yet.");
        foreach (var step in TableRunner.BuildRows<RanStep>(steps))
        {
            var stepId = $"step-{++_stepCounter}";
            var error = step.Status is "ok" or "success" ? null : $"{step.Text}: {step.Status}";
            await ingest(
                new StepStarted(current, uid, stepId, step.Kind, step.Text),
                new StepFinished(current, uid, stepId, step.Status, 250, error));
        }
    }

    /// <summary>One row of <see cref="GivenTheseStepsRan"/>.</summary>
    public record RanStep(string Kind, string Text, string Status);

    [Given("a retry of \"{uid}\" was scheduled as attempt {nextAttempt} because \"{reason}\"")]
    internal Task GivenARetryWasScheduled(string uid, int nextAttempt, string reason)
        => ingest(new RetryScheduled(current, uid, nextAttempt, "RetryInProcess", reason));

    [Given("the scenario \"{uid}\" finished as \"{outcome}\"")]
    internal Task GivenTheScenarioFinishedAs(string uid, string outcome) => scenarioFinished(uid, outcome, attempts: 1);

    [Given("the scenario \"{uid}\" finished as \"{outcome}\" after {attempts} attempts")]
    internal Task GivenTheScenarioFinishedAfter(string uid, string outcome, int attempts)
        => scenarioFinished(uid, outcome, attempts);

    /// <summary>
    /// A whole result set in one table, <c>| Uid | Outcome | Attempts |</c>: each row is a
    /// ScenarioStarted + ScenarioFinished pair, the way a worker reports a scenario it ran to completion.
    /// </summary>
    [Given("these scenarios have finished")]
    internal async Task GivenTheseScenariosHaveFinished(StepTable scenarios)
    {
        foreach (var scenario in TableRunner.BuildRows<FinishedScenario>(scenarios))
        {
            await scenarioStarted(scenario.Uid, attempt: 1);
            await scenarioFinished(scenario.Uid, scenario.Outcome, scenario.Attempts);
        }
    }

    /// <summary>One row of <see cref="GivenTheseScenariosHaveFinished"/>.</summary>
    public record FinishedScenario(string Uid, string Outcome, int Attempts);

    private Task scenarioFinished(string uid, string outcome, int attempts)
    {
        _outcomes[uid] = outcome;
        var error = outcome is "Failed" or "Aborted" ? $"{uid} {outcome.ToLowerInvariant()}" : null;
        return ingest(new ScenarioFinished(current, uid, outcome, attempts, 900, error));
    }

    /// <summary>
    /// A scenario_finished carrying the touched-type run evidence (issue #107), the way an
    /// evidence-aware publisher reports it. The comma-separated names travel as TouchedType records
    /// with a synthesized full name and assembly.
    /// </summary>
    [Given("the scenario \"{uid}\" finished touching \"{typeNames}\"")]
    internal Task GivenTheScenarioFinishedTouching(string uid, string typeNames)
    {
        _outcomes[uid] = "CleanPass";
        var touched = typeNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new TouchedType(name, $"Wallets.{name}", "Wallets"))
            .ToArray();
        return ingest(new ScenarioFinished(
            current, uid, "CleanPass", 1, 900, null,
            TouchedTypes: touched, At: DateTimeOffset.UtcNow));
    }

    [Given("the run has finished with exit code {exitCode}")]
    internal Task GivenTheRunHasFinished(int exitCode) => finishRun(exitCode);

    [When("the run finishes with exit code {exitCode}")]
    internal Task WhenTheRunFinishes(int exitCode) => finishRun(exitCode);

    /// <summary>
    /// The terminal event's counts are derived from what this test ingested, the way a real
    /// publisher tallies its own observer -- a clean pass and a pass-on-retry are counted apart.
    /// </summary>
    private Task finishRun(int exitCode)
        => ingest(new RunFinished(
            current,
            exitCode,
            Passed: _outcomes.Values.Count(o => o == "CleanPass"),
            Failed: _outcomes.Values.Count(o => o == "Failed"),
            PassedOnRetry: _outcomes.Values.Count(o => o == "PassOnRetry"),
            Indeterminate: 0,
            DateTimeOffset.UtcNow));

    // ---------------------------------------------------------------- event model (issue #108)

    /// <summary>
    /// Push a minimal descriptor over the public wire, the way a CI step pushes Wolverine's
    /// <c>event-model</c> export or what a spec assembly's generated source reported.
    /// </summary>
    [When("the event model \"{name}\" is published with slice \"{slice}\" bound to spec \"{spec}\"")]
    internal Task WhenTheEventModelIsPublished(string name, string slice, string spec)
        => putEventModel($$"""
            {
              "name": "{{name}}",
              "slices": [
                {
                  "name": "{{slice}}",
                  "pattern": "Command",
                  "commandType": { "name": "{{slice}}", "fullName": "Specs.{{slice}}", "assemblyName": "Specs" },
                  "emittedEvents": [],
                  "projectionTypes": [],
                  "readModelTypes": [],
                  "specifications": [{ "identity": "{{spec}}", "resolvedTypes": [] }]
                }
              ]
            }
            """);

    /// <summary>
    /// CritterWatch#1212 -- the HOST half. Slices and a command type, and deliberately NO
    /// specifications: this is what Wolverine's <c>event-model</c> export produces, because the
    /// spec assembly it would need is not one the host references.
    /// </summary>
    [When("the event model \"{name}\" is published with slice \"{slice}\" carrying no spec")]
    internal Task WhenTheEventModelIsPublishedWithoutSpec(string name, string slice)
        => putEventModel($$"""
            {
              "name": "{{name}}",
              "slices": [
                {
                  "name": "{{slice}}",
                  "pattern": "Command",
                  "commandType": { "name": "{{slice}}", "fullName": "Specs.{{slice}}", "assemblyName": "Specs" },
                  "emittedEvents": [],
                  "projectionTypes": [],
                  "readModelTypes": []
                }
              ]
            }
            """);

    /// <summary>
    /// CritterWatch#1212 -- the SPEC half, pushed under its own source. Carries the specification and
    /// nothing else, which is exactly what a spec assembly's generated source knows.
    /// </summary>
    [When("the source \"{source}\" publishes the event model \"{name}\" with slice \"{slice}\" bound to spec \"{spec}\"")]
    internal Task WhenTheSourcePublishesTheEventModel(string source, string name, string slice, string spec)
        => putEventModel($$"""
            {
              "name": "{{name}}",
              "slices": [
                {
                  "name": "{{slice}}",
                  "specifications": [{ "identity": "{{spec}}", "resolvedTypes": [] }]
                }
              ]
            }
            """, source);

    /// <summary>bobcat#298 -- a slice with a chapter, the way the emlang import or a chapter binding produces one.</summary>
    [When("the event model \"{name}\" is published with slice \"{slice}\" in chapter \"{chapter}\"")]
    internal Task WhenTheEventModelIsPublishedWithChapter(string name, string slice, string chapter)
        => putEventModel($$"""
            {
              "name": "{{name}}",
              "slices": [
                {
                  "name": "{{slice}}",
                  "pattern": "Command",
                  "chapter": "{{chapter}}",
                  "commandType": { "name": "{{slice}}", "fullName": "Specs.{{slice}}", "assemblyName": "Specs" },
                  "emittedEvents": [],
                  "projectionTypes": [],
                  "readModelTypes": []
                }
              ]
            }
            """);

    /// <summary>
    /// Issue #169 -- push a body that is NOT a descriptor, so the rejected path can be asserted on:
    /// a 400 must not announce a change, because nothing about what the page would load has moved.
    /// </summary>
    [When("something that is not an event model is published")]
    internal Task WhenSomethingThatIsNotAnEventModelIsPublished() => putEventModel("not a descriptor at all");

    [Then("the slice \"{slice}\" of the event model is in chapter \"{chapter}\"")]
    internal async Task ThenTheSliceIsInChapter(string slice, string chapter)
    {
        await fetchRaw("/api/event-model");
        fact(slices().Any(s => s.GetProperty("name").GetString() == slice
                                && s.TryGetProperty("chapter", out var c)
                                && c.GetString() == chapter),
            $"No slice '{slice}' in chapter '{chapter}' in: {_lastBody}");
    }

    /// <summary>The merge must keep the other source's contribution, not just accept the newer one.</summary>
    [Then("the slice \"{slice}\" of the event model still carries its command type")]
    internal async Task ThenTheSliceStillCarriesItsCommandType(string slice)
    {
        await fetchRaw("/api/event-model");
        fact(slices().Any(s => s.GetProperty("name").GetString() == slice
                                && s.TryGetProperty("commandType", out var c)
                                && c.ValueKind == JsonValueKind.Object),
            $"Slice '{slice}' lost its command type in: {_lastBody}");
    }

    [Then("the slice \"{slice}\" of the event model carries the spec identity \"{spec}\"")]
    internal async Task ThenTheSliceCarriesTheSpec(string slice, string spec)
    {
        await fetchRaw("/api/event-model");
        fact(carriesSpec(slice, spec), $"Slice '{slice}' does not carry '{spec}' in: {_lastBody}");
    }

    [Then("the slice \"{slice}\" of the event model does not carry the spec identity \"{spec}\"")]
    internal async Task ThenTheSliceDoesNotCarryTheSpec(string slice, string spec)
    {
        await fetchRaw("/api/event-model");
        fact(!carriesSpec(slice, spec), $"Slice '{slice}' still carries '{spec}' in: {_lastBody}");
    }

    private bool carriesSpec(string slice, string spec)
        => slices().Any(s => s.GetProperty("name").GetString() == slice
                             && s.GetProperty("specifications").EnumerateArray()
                                 .Any(x => x.GetProperty("identity").GetString() == spec));

    private IEnumerable<JsonElement> slices()
        => JsonDocument.Parse(_lastBody).RootElement.GetProperty("slices").EnumerateArray();

    /// <summary>
    /// PUT the body and capture both the status and any <see cref="EventModelChanged"/> the console
    /// broadcast (#169), so one step serves the wire assertions and the push assertion.
    /// </summary>
    private async Task putEventModel(string json, string? source = null)
    {
        var url = source is null ? "/api/event-model" : $"/api/event-model/{source}";
        // A presence assertion, deliberately: the session waits for activity to settle and the
        // checks below then look for a message. No exception/timeout suppression -- if the console
        // stops broadcasting, this must fail loudly rather than pass over silence.
        var tracked = await host.Host
            .TrackActivity()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync((Func<Wolverine.IMessageContext, Task>)(async _ =>
            {
                var result = await host.AlbaHost.Scenario(s =>
                {
                    s.Put.Text(json).ToUrl(url);
                    s.IgnoreStatusCode();
                });

                _lastStatus = result.Context.Response.StatusCode;
            }));

        _broadcastModelNames = tracked.Sent.MessagesOf<EventModelChanged>()
            .Select(x => x.Name)
            .ToArray();
    }

    /// <summary>
    /// Issue #169 -- the page reads GET /api/event-model on load only, so without this broadcast even
    /// a successful push needed an F5. Paired with Wolverine's `event-model --url`, a `dotnet watch`
    /// redraws the diagram on every save.
    /// </summary>
    [Then("the event model change is broadcast for \"{name}\"")]
    internal void ThenTheEventModelChangeIsBroadcast(string name)
        => fact(_broadcastModelNames.Contains(name),
            $"No EventModelChanged for '{name}'; broadcast: [{string.Join(", ", _broadcastModelNames)}]");

    [Then("no event model change is broadcast")]
    internal void ThenNoEventModelChangeIsBroadcast()
        => fact(_broadcastModelNames.Length == 0,
            $"Expected no EventModelChanged, but got: [{string.Join(", ", _broadcastModelNames)}]");

    [Then("asking for the event model responds with status {status}")]
    internal async Task ThenAskingForTheEventModelRespondsWith(int status)
    {
        await fetchRaw("/api/event-model");
        check("status", _lastStatus, status);
    }

    [Then("the published event model is named \"{name}\"")]
    internal async Task ThenThePublishedEventModelIsNamed(string name)
    {
        await fetchRaw("/api/event-model");
        check("name", JsonDocument.Parse(_lastBody).RootElement.GetProperty("name").GetString() ?? "", name);
    }

    // ---------------------------------------------------------------- actions (When)

    [When("the run is ejected")]
    internal Task WhenTheRunIsEjected() => eject(current);

    [When("an unknown run is ejected")]
    internal Task WhenAnUnknownRunIsEjected() => eject(Guid.NewGuid());

    // Issue #197 -- the bulk verbs, in the browser tab menu's shape.

    [When("every run is ejected")]
    internal Task WhenEveryRunIsEjected() => ejectMany("/api/runs");

    [When("every run older than \"{suite}\" is ejected")]
    internal async Task WhenEveryRunOlderThanIsEjected(string suite)
    {
        var anchor = (await runList()).Single(r => r.RunId == _runs[suite]);
        await ejectMany($"/api/runs?olderThan={Uri.EscapeDataString(anchor.StartedAt!.Value.ToString("O"))}");
    }

    [When("every run but \"{suite}\" is ejected")]
    internal Task WhenEveryRunButIsEjected(string suite) => ejectMany($"/api/runs?exceptRunId={_runs[suite]}");

    /// <summary>stoat#83: the slug is sent upper-cased, because case never splits a repository.</summary>
    [When("every run in this checkout's repository is ejected")]
    internal Task WhenEveryRunInThisRepositoryIsEjected()
        => ejectMany($"/api/runs?repository={Uri.EscapeDataString(GitRemotes.SlugForRoot(thisCheckout())!.ToUpperInvariant())}");

    [When("every unattributed run is ejected")]
    internal Task WhenEveryUnattributedRunIsEjected() => ejectMany("/api/runs?unattributed=true");

    private async Task ejectMany(string url)
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Delete.Url(url);
            s.IgnoreStatusCode();
        });

        _lastStatus = result.Context.Response.StatusCode;
        _lastBody = await result.ReadAsTextAsync();
    }

    [Then("the bulk eject reports {count} runs taken")]
    internal void ThenTheBulkEjectReports(int count)
        => check("count", JsonDocument.Parse(_lastBody).RootElement.GetProperty("count").GetInt32(), count);

    private async Task eject(Guid runId)
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Delete.Url($"/api/runs/{runId}");
            s.IgnoreStatusCode();
        });
        _lastStatus = result.Context.Response.StatusCode;
    }

    [When("the viewer restarts")]
    internal Task WhenTheViewerRestarts() => host.Restart();

    [When("the run is exported as {format}")]
    internal Task WhenTheRunIsExportedAs(string format) => fetchRaw($"/api/runs/{current}/export?format={format}");

    // ---------------------------------------------------------------- the run list (Then)

    [Then("the run \"{suite}\" appears in the run list")]
    internal async Task ThenTheRunAppears(string suite)
    {
        var runs = await runList();
        fact(runs.Any(r => r.RunId == _runs[suite] && r.Suite == suite),
            $"'{suite}' is not in the run list: [{string.Join(", ", runs.Select(r => r.Suite))}]");
    }

    [Then("the run \"{suite}\" is not in the run list")]
    internal async Task ThenTheRunIsNotInTheRunList(string suite)
    {
        var runs = await runList();
        fact(runs.All(r => r.RunId != _runs[suite]), $"'{suite}' is still in the run list");
    }

    [Then("the run list filtered by tag \"{tag}\" shows only \"{suites}\"")]
    internal async Task ThenTheRunListFilteredByTagShowsOnly(string tag, string suites)
    {
        var listed = await getJson<RunSummary[]>($"/api/runs?tag={Uri.EscapeDataString(tag)}") ?? [];
        var mine = listed.Where(r => _runs.ContainsValue(r.RunId)).ToArray();
        check("runs", string.Join(", ", mine.Select(r => r.Suite)), suites);
    }

    [Then("the run is listed as {state}")]
    internal async Task ThenTheRunIsListedAs(string state)
    {
        var run = await summary();
        var actual = run switch
        {
            { Orphaned: true } => "orphaned",
            { Finished: true } => "finished",
            _ => "running"
        };
        check("state", actual, state);
    }

    [Then("the run's summary reports {finished} of {total} scenarios finished")]
    internal async Task ThenTheRunsSummaryReportsProgress(int finished, int total)
    {
        var run = await summary();
        check("progress", $"{run.ScenariosFinished} of {run.TotalScenarios}", $"{finished} of {total}");
    }

    // The three counts are compared as one string so the whole verdict is compared at once; a
    // per-count check would pass "1 passed" on a run that also lost the flakiness ledger.
    [Then("the run's summary reports {passed} passed, {failed} failed and {passedOnRetry} passed on retry")]
    internal async Task ThenTheRunsSummaryReportsTheVerdict(int passed, int failed, int passedOnRetry)
    {
        var run = await summary();
        check("verdict",
            $"{run.Passed} passed, {run.Failed} failed and {run.PassedOnRetry} passed on retry",
            $"{passed} passed, {failed} failed and {passedOnRetry} passed on retry");
    }

    [Then("the run's exit code is {exitCode}")]
    internal async Task ThenTheRunsExitCodeIs(int exitCode) => check("exitCode", (await summary()).ExitCode ?? -1, exitCode);

    // ---------------------------------------------------------------- one run (Then)

    [Then("the scenario \"{uid}\" shows {attempts} attempts")]
    internal async Task ThenTheScenarioShowsAttempts(string uid, int attempts)
        => check("attempts", (await scenario(uid)).Attempts ?? 0, attempts);

    [Then("the outcome of \"{uid}\" is \"{outcome}\"")]
    internal async Task ThenTheOutcomeIs(string uid, string outcome)
        => check("outcome", (await scenario(uid)).Outcome ?? "(running)", outcome);

    [Then("the retry reasons for \"{uid}\" include \"{reason}\"")]
    internal async Task ThenTheRetryReasonsInclude(string uid, string reason)
    {
        var reasons = (await scenario(uid)).RetryReasons;
        fact(reasons.Contains(reason), $"Retry reasons are [{string.Join(", ", reasons)}]");
    }

    [Then("the final attempt of \"{uid}\" ran {steps} steps")]
    internal async Task ThenTheFinalAttemptRan(string uid, int steps)
        => check("steps", (await scenario(uid)).Steps.Length, steps);

    [Then("the touched types of \"{uid}\" are \"{typeNames}\"")]
    internal async Task ThenTheTouchedTypesAre(string uid, string typeNames)
        => check("touched", string.Join(", ", (await scenario(uid)).TouchedTypes.Select(t => t.Name)), typeNames);

    [Then("the evidence for \"{uid}\" is stamped with a finish time")]
    internal async Task ThenTheEvidenceIsStamped(string uid)
        => fact((await scenario(uid)).FinishedAt != null, $"'{uid}' carries no finish time");

    [When("a batch with no events is ingested")]
    internal async Task WhenABatchWithNoEventsIsIngested()
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Post.Json(new { }).ToUrl("/api/ingest");
            s.IgnoreStatusCode();
        });
        _lastStatus = result.Context.Response.StatusCode;
    }

    [Then("the ingestion responds with status {status}")]
    internal void ThenTheIngestionRespondsWith(int status) => check("status", _lastStatus, status);

    [Then("asking for the run responds with status {status}")]
    internal async Task ThenAskingForTheRunRespondsWith(int status)
    {
        await fetchRaw($"/api/runs/{current}");
        check("status", _lastStatus, status);
    }

    // ---------------------------------------------------------------- archives on disk (Then)

    [Then("the run's archive is on disk")]
    internal void ThenTheRunsArchiveIsOnDisk()
        => fact(File.Exists(host.ArchiveFileFor(current)), $"No archive at {host.ArchiveFileFor(current)}");

    [Then("the run's archive has moved to the ejected folder")]
    internal void ThenTheRunsArchiveHasMovedToTheEjectedFolder()
        => fact(File.Exists(host.EjectedFileFor(current)) && !File.Exists(host.ArchiveFileFor(current)),
            $"Expected the archive at {host.EjectedFileFor(current)} and none at {host.ArchiveFileFor(current)}");

    // ---------------------------------------------------------------- responses and exports (Then)

    [Then("the eject responds with status {status}")]
    internal void ThenTheEjectRespondsWith(int status) => check("status", _lastStatus, status);

    [Then("the export responds with status {status}")]
    internal void ThenTheExportRespondsWith(int status) => check("status", _lastStatus, status);

    [Then("the export is served as \"{contentType}\"")]
    internal void ThenTheExportIsServedAs(string contentType)
        => check("contentType", _lastContentType?.Split(';')[0].Trim() ?? "(none)", contentType);

    [Then("the CTRF summary counts {passed} passed, {failed} failed and {flaky} flaky")]
    internal void ThenTheCtrfSummaryCounts(int passed, int failed, int flaky)
    {
        var summary = ctrf().GetProperty("results").GetProperty("summary");
        check("summary",
            $"{summary.GetProperty("passed").GetInt32()} passed, " +
            $"{summary.GetProperty("failed").GetInt32()} failed and " +
            $"{summary.GetProperty("flaky").GetInt32()} flaky",
            $"{passed} passed, {failed} failed and {flaky} flaky");
    }

    [Then("the CTRF test \"{name}\" has {retries} retries")]
    internal void ThenTheCtrfTestHasRetries(string name, int retries)
        => check("retries", ctrfTest(name).GetProperty("retries").GetInt32(), retries);

    [Then("the CTRF test \"{name}\" is flaky")]
    internal void ThenTheCtrfTestIsFlaky(string name)
        => fact(ctrfTest(name).GetProperty("flaky").GetBoolean(), $"'{name}' is not flaky");

    [Then("the CTRF status of \"{name}\" is {status}")]
    internal void ThenTheCtrfStatusIs(string name, string status)
        => check("status", ctrfTest(name).GetProperty("status").GetString() ?? "", status);

    [Then("the JUnit report counts {tests} tests and {failures} failures")]
    internal void ThenTheJUnitReportCounts(int tests, int failures)
    {
        var suites = XDocument.Parse(_lastBody).Root!;
        check("counts",
            $"{suites.Attribute("tests")?.Value} tests and {suites.Attribute("failures")?.Value} failures",
            $"{tests} tests and {failures} failures");
    }

    [Then("the NDJSON export has {events} events")]
    internal void ThenTheNdjsonExportHas(int events)
        => check("events", _lastBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length, events);

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// One expected/actual cell on the step in progress, so the verdict renders in the sentence. Outside a
    /// recording there is no step to carry it, so a mismatch throws instead: a check must never pass by
    /// having nowhere to report.
    /// </summary>
    private static void check<T>(string name, T actual, T expected)
    {
        if (!SpecAssert.Check(name, actual, expected) && ScenarioRecorder.CurrentStep is null)
            throw new SpecAssertionException($"{name}: expected '{expected}', got '{actual}'");
    }

    /// <inheritdoc cref="check{T}"/>
    private static void fact(bool condition, string because)
    {
        if (!SpecAssert.Fact(condition, because) && ScenarioRecorder.CurrentStep is null)
            throw new SpecAssertionException(because);
    }

    private async Task ingest(params MonitorEvent[] events)
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Post.Json(new IngestBatch(events)).ToUrl("/api/ingest");
            s.IgnoreStatusCode();
        });

        var status = result.Context.Response.StatusCode;
        if (status != 202)
            throw new InvalidOperationException($"POST /api/ingest answered {status}, expected 202 Accepted");
    }

    private async Task<T?> getJson<T>(string url)
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Get.Url(url);
            s.IgnoreStatusCode();
        });

        try { return result.ReadAsJson<T>(); }
        catch { return default; }
    }

    private async Task<RunSummary[]> runList() => await getJson<RunSummary[]>("/api/runs") ?? [];

    private async Task<RunSummary> summary()
        => (await runList()).FirstOrDefault(r => r.RunId == current)
           ?? throw new InvalidOperationException($"run {current} is not in GET /api/runs");

    private async Task<ScenarioResult> scenario(string uid)
    {
        var detail = await getJson<RunDetail>($"/api/runs/{current}")
                     ?? throw new InvalidOperationException($"GET /api/runs/{current} answered no run");

        return detail.Scenarios.FirstOrDefault(s => s.Uid == uid)
               ?? throw new InvalidOperationException(
                   $"scenario '{uid}' is not in the run; known: {string.Join(", ", detail.Scenarios.Select(s => s.Uid))}");
    }

    /// <summary>The raw body and the content type as well as the status, which the exports need.</summary>
    private async Task fetchRaw(string url)
    {
        var result = await host.AlbaHost.Scenario(s =>
        {
            s.Get.Url(url);
            s.IgnoreStatusCode();
        });

        _lastStatus = result.Context.Response.StatusCode;
        _lastContentType = result.Context.Response.ContentType;
        _lastBody = await result.ReadAsTextAsync();
    }

    private JsonElement ctrf() => JsonDocument.Parse(_lastBody).RootElement;

    private JsonElement ctrfTest(string name)
    {
        foreach (var test in ctrf().GetProperty("results").GetProperty("tests").EnumerateArray())
        {
            if (test.GetProperty("name").GetString() == name) return test;
        }

        throw new InvalidOperationException($"no CTRF test named '{name}' in the export");
    }

    private static (string Feature, string Scenario) split(string uid)
    {
        var slash = uid.IndexOf('/');
        return slash > 0 ? (uid[..slash], uid[(slash + 1)..]) : ("", uid);
    }
}
