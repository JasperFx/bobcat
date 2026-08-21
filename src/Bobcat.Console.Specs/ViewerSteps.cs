using System.Text.Json;
using System.Xml.Linq;
using Alba;
using Bobcat.Alba;
using Bobcat.Console.Contracts;

namespace Bobcat.Console.Specs;

/// <summary>
/// The whole vocabulary of the viewer suite, shared across its features with
/// <c>[IncludeGrammars]</c>. Givens ingest events over <c>POST /api/ingest</c> exactly as a
/// publisher would; Whens hit the eject, restart and export seams; Thens read the public wire
/// back — <c>GET /api/runs</c>, <c>GET /api/runs/{id}</c>, and the exports.
/// </summary>
/// <remarks>
/// One module rather than steps on each feature's fixture because the vocabulary is one
/// vocabulary: every feature starts a run and ingests events. The generator discovers steps on
/// the fixture's own declared members and on included modules, not on a base class, so a shared
/// base fixture would silently contribute nothing (the module is the supported composition).
/// Inherits <see cref="Fixture"/> so it receives the step context; a fresh instance per scenario
/// is what makes the fields below per-scenario state.
/// </remarks>
public class ViewerSteps : Fixture
{
    private readonly Dictionary<string, Guid> _runs = new();
    private readonly Dictionary<string, string> _outcomes = new();
    private Guid _current;
    private string? _currentUid;
    private int _stepCounter;

    private int _lastStatus;
    private string? _lastContentType;
    private string _lastBody = "";

    private Guid current => _current == Guid.Empty
        ? throw new InvalidOperationException("No run has been started in this scenario yet.")
        : _current;

    // ---------------------------------------------------------------- ingestion (Given)

    [Given("a run {string} has started")]
    public Task RunStarted(string suite) => startRun(suite, totalScenarios: null, tag: null);

    [Given("a run {string} has started with {int} scenarios")]
    public Task RunStartedWithTotal(string suite, int total) => startRun(suite, total, tag: null);

    [Given("a run {string} tagged {string} has started")]
    public Task RunStartedWithTag(string suite, string tag) => startRun(suite, totalScenarios: null, tag);

    private async Task startRun(string suite, int? totalScenarios, string? tag)
    {
        var runId = Guid.NewGuid();
        _runs[suite] = runId;
        _current = runId;
        _outcomes.Clear();

        await ingest(new RunStarted(
            runId, suite, "/repo/bobcat", "main", "in-process",
            DateTimeOffset.UtcNow, totalScenarios, tag));
    }

    [Given("the scenario {string} has started")]
    public Task ScenarioStarted(string uid) => scenarioStarted(uid, attempt: 1);

    [Given("the scenario {string} started its attempt {int}")]
    public Task ScenarioStartedAttempt(string uid, int attempt) => scenarioStarted(uid, attempt);

    private Task scenarioStarted(string uid, int attempt)
    {
        _currentUid = uid;
        var (feature, scenario) = split(uid);
        return ingest(new ScenarioStarted(current, uid, feature, scenario, attempt, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Steps for the scenario most recently started. The uid would read better in the step text
    /// ("these steps ran in {string}"), but a [Table] step binds its parameters from the table's
    /// columns only — an expression capture on a table step arrives as default, not the captured
    /// value — so the scenario is implicit. Part of the #86 finding.
    /// </summary>
    [Given("these steps ran")]
    [Table]
    public async Task StepsRan(string kind, string text, string status)
    {
        var uid = _currentUid ?? throw new InvalidOperationException("No scenario has started yet.");
        var stepId = $"step-{++_stepCounter}";
        var error = status is "ok" or "success" ? null : $"{text}: {status}";
        await ingest(
            new StepStarted(current, uid, stepId, kind, text),
            new StepFinished(current, uid, stepId, status, 250, error));
    }

    [Given("a retry of {string} was scheduled as attempt {int} because {string}")]
    public Task RetryScheduled(string uid, int nextAttempt, string reason)
        => ingest(new RetryScheduled(current, uid, nextAttempt, "RetryInProcess", reason));

    [Given("the scenario {string} finished as {string}")]
    public Task ScenarioFinished(string uid, string outcome) => scenarioFinished(uid, outcome, attempts: 1);

    [Given("the scenario {string} finished as {string} after {int} attempts")]
    public Task ScenarioFinishedAfter(string uid, string outcome, int attempts)
        => scenarioFinished(uid, outcome, attempts);

    /// <summary>
    /// A whole result set in one table: each row is a ScenarioStarted + ScenarioFinished pair,
    /// the way a worker reports a scenario it ran to completion.
    /// </summary>
    [Given("these scenarios have finished")]
    [Table]
    public async Task ScenariosFinished(string uid, string outcome, int attempts)
    {
        await scenarioStarted(uid, attempt: 1);
        await scenarioFinished(uid, outcome, attempts);
    }

    private Task scenarioFinished(string uid, string outcome, int attempts)
    {
        _outcomes[uid] = outcome;
        var error = outcome is "Failed" or "Aborted" ? $"{uid} {outcome.ToLowerInvariant()}" : null;
        return ingest(new ScenarioFinished(current, uid, outcome, attempts, 900, error));
    }

    [Given("the run has finished with exit code {int}")]
    public Task RunHasFinished(int exitCode) => finishRun(exitCode);

    [When("the run finishes with exit code {int}")]
    public Task RunFinishes(int exitCode) => finishRun(exitCode);

    /// <summary>
    /// The terminal event's counts are derived from what this scenario ingested, the way a real
    /// publisher tallies its own observer — a clean pass and a pass-on-retry are counted apart.
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

    // ---------------------------------------------------------------- actions (When)

    [When("the run is ejected")]
    public Task RunEjected() => eject(current);

    [When("an unknown run is ejected")]
    public Task UnknownRunEjected() => eject(Guid.NewGuid());

    private async Task eject(Guid runId)
    {
        var result = await Context!.DeleteAsync($"/api/runs/{runId}");
        _lastStatus = result.StatusCode;
    }

    [When("the viewer restarts")]
    public Task ViewerRestarts() => Context!.GetResource<MonitorHost>().Restart();

    [When("the run is exported as {word}")]
    public Task RunExported(string format) => fetchRaw($"/api/runs/{current}/export?format={format}");

    [When("the run is asked for")]
    public Task RunAskedFor() => fetchRaw($"/api/runs/{current}");

    // ---------------------------------------------------------------- the run list (Then)

    [Check("the run {string} appears in the run list")]
    public async Task<bool> RunAppears(string suite)
    {
        var runs = await runList();
        return runs.Any(r => r.RunId == _runs[suite] && r.Suite == suite);
    }

    [Check("the run {string} is not in the run list")]
    public async Task<bool> RunIsAbsent(string suite)
    {
        var runs = await runList();
        return runs.All(r => r.RunId != _runs[suite]);
    }

    [Then("the run list filtered by tag {string} shows only {string}")]
    public async Task<string> RunListByTag(string tag)
    {
        var result = await Context!.GetJsonAsync<RunSummary[]>($"/api/runs?tag={Uri.EscapeDataString(tag)}");
        var mine = (result.Body ?? []).Where(r => _runs.ContainsValue(r.RunId)).ToArray();
        return string.Join(", ", mine.Select(r => r.Suite));
    }

    [Then("the run is listed as {word}")]
    public async Task<string> RunState()
    {
        var run = await summary();
        return run switch
        {
            { Orphaned: true } => "orphaned",
            { Finished: true } => "finished",
            _ => "running"
        };
    }

    [Then("the run's summary reports {int} of {int} scenarios finished")]
    public async Task<string> RunProgress()
    {
        var run = await summary();
        return $"{run.ScenariosFinished} of {run.TotalScenarios}";
    }

    // The three counts travel as one string so the whole verdict is compared at once; a
    // per-count Then would pass "1 passed" on a run that also lost the flakiness ledger.
    [Then("the run's summary reports {int} passed, {int} failed and {int} passed on retry")]
    public async Task<string> RunVerdict()
    {
        var run = await summary();
        return $"{run.Passed} passed, {run.Failed} failed and {run.PassedOnRetry} passed on retry";
    }

    [Then("the run's exit code is {int}")]
    public async Task<int> RunExitCode() => (await summary()).ExitCode ?? -1;

    // ---------------------------------------------------------------- one run (Then)

    [Then("the scenario {string} shows {int} attempts")]
    public async Task<int> ScenarioAttempts(string uid) => (await scenario(uid)).Attempts ?? 0;

    [Then("the outcome of {string} is {string}")]
    public async Task<string> ScenarioOutcome(string uid) => (await scenario(uid)).Outcome ?? "(running)";

    [Check("the retry reasons for {string} include {string}")]
    public async Task<bool> RetryReasonsInclude(string uid, string reason)
        => (await scenario(uid)).RetryReasons.Contains(reason);

    [Then("the final attempt of {string} ran {int} steps")]
    public async Task<int> FinalAttemptSteps(string uid) => (await scenario(uid)).Steps.Length;

    [Then("asking for the run responds with status {int}")]
    public async Task<int> AskingForTheRun()
    {
        await fetchRaw($"/api/runs/{current}");
        return _lastStatus;
    }

    // ---------------------------------------------------------------- archives on disk (Then)

    [Check("the run's archive is on disk")]
    public bool ArchiveOnDisk() => File.Exists(host.ArchiveFileFor(current));

    [Check("the run's archive has moved to the ejected folder")]
    public bool ArchiveEjected()
        => File.Exists(host.EjectedFileFor(current)) && !File.Exists(host.ArchiveFileFor(current));

    // ---------------------------------------------------------------- responses and exports (Then)

    [Then("the eject responds with status {int}")]
    public int EjectStatus() => _lastStatus;

    [Then("the export responds with status {int}")]
    public int ExportStatus() => _lastStatus;

    [Then("the export is served as {string}")]
    public string ExportContentType() => _lastContentType?.Split(';')[0].Trim() ?? "(none)";

    [Then("the CTRF summary counts {int} passed, {int} failed and {int} flaky")]
    public string CtrfSummary()
    {
        var summary = ctrf().GetProperty("results").GetProperty("summary");
        return $"{summary.GetProperty("passed").GetInt32()} passed, " +
               $"{summary.GetProperty("failed").GetInt32()} failed and " +
               $"{summary.GetProperty("flaky").GetInt32()} flaky";
    }

    [Then("the CTRF test {string} has {int} retries")]
    public int CtrfRetries(string name) => ctrfTest(name).GetProperty("retries").GetInt32();

    [Check("the CTRF test {string} is flaky")]
    public bool CtrfIsFlaky(string name) => ctrfTest(name).GetProperty("flaky").GetBoolean();

    [Then("the CTRF status of {string} is {word}")]
    public string CtrfStatus(string name) => ctrfTest(name).GetProperty("status").GetString() ?? "";

    [Then("the JUnit report counts {int} tests and {int} failures")]
    public string JUnitCounts()
    {
        var suites = XDocument.Parse(_lastBody).Root!;
        return $"{suites.Attribute("tests")?.Value} tests and {suites.Attribute("failures")?.Value} failures";
    }

    [Then("the NDJSON export has {int} events")]
    public int NdjsonEvents()
        => _lastBody.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    // ---------------------------------------------------------------- plumbing

    private MonitorHost host => Context!.GetResource<MonitorHost>();

    private async Task ingest(params MonitorEvent[] events)
    {
        var result = await Context!.PostJsonAsync<IngestBatch, object>("/api/ingest", new IngestBatch(events));
        if (result.StatusCode != 202)
            throw new InvalidOperationException($"POST /api/ingest answered {result.StatusCode}, expected 202 Accepted");
    }

    private async Task<RunSummary[]> runList()
        => (await Context!.GetJsonAsync<RunSummary[]>("/api/runs")).Body ?? [];

    private async Task<RunSummary> summary()
        => (await runList()).FirstOrDefault(r => r.RunId == current)
           ?? throw new InvalidOperationException($"run {current} is not in GET /api/runs");

    private async Task<ScenarioResult> scenario(string uid)
    {
        var result = await Context!.GetJsonAsync<RunDetail>($"/api/runs/{current}");
        if (result.Body == null)
            throw new InvalidOperationException($"GET /api/runs/{current} answered {result.StatusCode}");

        return result.Body.Scenarios.FirstOrDefault(s => s.Uid == uid)
               ?? throw new InvalidOperationException(
                   $"scenario '{uid}' is not in the run; known: {string.Join(", ", result.Body.Scenarios.Select(s => s.Uid))}");
    }

    /// <summary>
    /// The Bobcat.Alba helpers return a typed body and a status code; the export endpoints need
    /// the raw body and the content type as well, so this reaches into Alba directly.
    /// </summary>
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
