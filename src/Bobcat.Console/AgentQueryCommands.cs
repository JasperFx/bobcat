using System.Net;
using Bobcat.Ui;
using JasperFx.CommandLine;

namespace Bobcat.Console;

// bobcat#524 — the agent-facing reads over a running `bobcat ui`, as commands rather than MCP
// tools: an agent asks with a shell command and gets JSON back on stdout. Each command is one
// GET against /api/agent/* (Bobcat.Ui.Agent.AgentEndpoints); the console does the work, because
// the board and the Event Model are in its memory, not on this process's disk.
//
// Exit codes: 0 with the answer on stdout; 1 when the console refused (the {"error": …} body goes
// to stderr) or nothing is listening.

public class AgentQueryInput
{
    [Description("Base URL of the Bobcat UI; defaults to http://localhost:5525")]
    [FlagAlias("url", 'u')]
    public string? UrlFlag { get; set; }

    internal string BaseUrl => (string.IsNullOrWhiteSpace(UrlFlag) ? BobcatUiHost.UrlFor(BobcatUiHost.DefaultPort) : UrlFlag.Trim()).TrimEnd('/');
}

public class RunQueryInput : AgentQueryInput
{
    [Description("Run id (from list-runs); defaults to the most recent run")]
    [FlagAlias("run", 'r')]
    public string? RunFlag { get; set; }
}

public class FailingSpecInput : RunQueryInput
{
    [Description("Spec identity {Feature}/{Scenario}; defaults to the run's first failing scenario")]
    [FlagAlias("uid", true)]
    public string? UidFlag { get; set; }
}

public class EventModelQueryInput : AgentQueryInput
{
    [Description("Narrow to the one slice with this name (case-insensitive)")]
    public string? SliceFlag { get; set; }

    [Description("Narrow to the slices in this domain (case-insensitive)")]
    public string? DomainFlag { get; set; }
}

public class ExportRunInput : RunQueryInput
{
    [Description("ctrf (rich JSON, the default) or junit (lossy XML for CI)")]
    [FlagAlias("format", 'f')]
    public string FormatFlag { get; set; } = "ctrf";

    [Description("Write to this file instead of stdout")]
    [FlagAlias("output", 'o')]
    public string? OutputFlag { get; set; }
}

public class FlakyLedgerInput : AgentQueryInput
{
    // Not RunQueryInput: this reads every run unless narrowed, never "the most recent" (bobcat#660).
    [Description("Narrow to one run id (from list-runs); every run the UI knows by default")]
    [FlagAlias("run", 'r')]
    public string? RunFlag { get; set; }
}

public class AwaitRunInput : AgentQueryInput
{
    // Not RunQueryInput: with no id this waits on the run in flight (bobcat#660).
    [Description("Run id to wait on (from list-runs); defaults to the run in flight, or the most recent run when none is")]
    [FlagAlias("run", 'r')]
    public string? RunFlag { get; set; }

    [Description("Seconds to wait before answering 'timeout' with the run's progress; default 600, at most 3600")]
    public int TimeoutFlag { get; set; } = 600;
}

internal static class AgentQuery
{
    /// <summary>GET <paramref name="pathAndQuery"/> and write the answer; false on a refusal or no console.</summary>
    public static async Task<bool> RunAsync(AgentQueryInput input, string pathAndQuery, string? outputFile = null, TimeSpan? timeout = null)
    {
        using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        var url = input.BaseUrl + pathAndQuery;

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            System.Console.Error.WriteLine(
                $"Nothing answered at {input.BaseUrl}. These commands read a running Bobcat UI — start one with `bobcat ui`" +
                (input.UrlFlag is null ? "" : ", or check --url") + $". ({e.Message})");
            return false;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                System.Console.Error.WriteLine(
                    $"{input.BaseUrl} answered 404 for {pathAndQuery}. It may be a console older than these commands (a Stoat host, " +
                    "or a bobcat ui from before bobcat#524).");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                System.Console.Error.WriteLine(body);
                return false;
            }

            if (outputFile is not null)
            {
                await File.WriteAllTextAsync(outputFile, body);
                System.Console.WriteLine($"Wrote {outputFile}");
            }
            else
            {
                System.Console.WriteLine(body);
            }

            return true;
        }
    }

    public static string Query(params (string Name, string? Value)[] parameters)
    {
        var present = parameters.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToArray();
        return present.Length == 0
            ? string.Empty
            : "?" + string.Join("&", present.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!.Trim())}"));
    }
}

[Description("List the test runs a running Bobcat UI knows, newest first (JSON)", Name = "list-runs")]
public class ListRunsCommand : JasperFxAsyncCommand<AgentQueryInput>
{
    public override Task<bool> Execute(AgentQueryInput input) => AgentQuery.RunAsync(input, "/api/agent/runs");
}

[Description("Progress detail for one run: scenarios, live steps, lanes, faults, stalls (JSON)", Name = "run-status")]
public class RunStatusCommand : JasperFxAsyncCommand<RunQueryInput>
{
    public override Task<bool> Execute(RunQueryInput input)
        => AgentQuery.RunAsync(input, "/api/agent/run-status" + AgentQuery.Query(("runId", input.RunFlag)));
}

[Description("Every failed or aborted scenario in a run, with errors and failing steps (JSON)", Name = "failing-tests")]
public class FailingTestsCommand : JasperFxAsyncCommand<RunQueryInput>
{
    public override Task<bool> Execute(RunQueryInput input)
        => AgentQuery.RunAsync(input, "/api/agent/failing-tests" + AgentQuery.Query(("runId", input.RunFlag)));
}

[Description("Full detail for one scenario: every step, cells, logs, exception frames, touched types and bound slices (JSON)", Name = "failing-spec")]
public class FailingSpecCommand : JasperFxAsyncCommand<FailingSpecInput>
{
    public override Task<bool> Execute(FailingSpecInput input)
        => AgentQuery.RunAsync(input, "/api/agent/failing-spec" + AgentQuery.Query(("runId", input.RunFlag), ("uid", input.UidFlag)));
}

[Description("Scenarios that passed only after retrying, across every known run (JSON)", Name = "flaky-ledger")]
public class FlakyLedgerCommand : JasperFxAsyncCommand<FlakyLedgerInput>
{
    public override Task<bool> Execute(FlakyLedgerInput input)
        => AgentQuery.RunAsync(input, "/api/agent/flaky-ledger" + AgentQuery.Query(("runId", input.RunFlag)));
}

[Description("The Event Model a running Bobcat UI holds, optionally one slice or domain (JSON)", Name = "event-model")]
public class EventModelQueryCommand : JasperFxAsyncCommand<EventModelQueryInput>
{
    public override Task<bool> Execute(EventModelQueryInput input)
        => AgentQuery.RunAsync(input, "/api/agent/event-model" + AgentQuery.Query(("slice", input.SliceFlag), ("domain", input.DomainFlag)));
}

[Description("What is untested per Event Model slice: no-spec versus no-evidence (JSON)", Name = "slice-coverage")]
public class SliceCoverageCommand : JasperFxAsyncCommand<AgentQueryInput>
{
    public override Task<bool> Execute(AgentQueryInput input) => AgentQuery.RunAsync(input, "/api/agent/slice-coverage");
}

[Description("Export a run's results as CTRF or JUnit", Name = "export-run")]
public class ExportRunCommand : JasperFxAsyncCommand<ExportRunInput>
{
    public override Task<bool> Execute(ExportRunInput input)
        => AgentQuery.RunAsync(input,
            "/api/agent/export" + AgentQuery.Query(("runId", input.RunFlag), ("format", input.FormatFlag)),
            outputFile: input.OutputFlag);
}

[Description("Wait for a run to finish, then print its summary (JSON) — instead of polling", Name = "await-run")]
public class AwaitRunCommand : JasperFxAsyncCommand<AwaitRunInput>
{
    public override Task<bool> Execute(AwaitRunInput input)
    {
        var seconds = Math.Clamp(input.TimeoutFlag, 1, 3600);
        return AgentQuery.RunAsync(input,
            "/api/agent/await-run" + AgentQuery.Query(("runId", input.RunFlag), ("timeoutSeconds", seconds.ToString())),
            timeout: TimeSpan.FromSeconds(seconds + 30));
    }
}
