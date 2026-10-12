using System.Diagnostics;
using Bobcat.Tests.Monitoring;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// Bobcat#488, end to end: a real projected xUnit v3 host, <c>Bobcat.Xunit.Samples</c>, whose real
/// generated <c>BobcatEventModelSource</c> comes from <c>Specs/SliceSpecs.cs</c>, run as a real
/// process against <see cref="FakeMonitorHost"/>, which captures every <c>PUT /api/event-model</c>.
/// </summary>
/// <remarks>
/// <para>
/// Before #488 only the Gherkin runner sent a spec assembly's half of the Event Model, so with that
/// lane gone a console got no spec half from any suite at all. The projected run bracket sends it
/// now, and these pin when: once per one-shot run, once per run request in a warm host (which is
/// what the Gherkin resident runner did too), never on discovery, never with no console, and never
/// when the console already serves a differently-named model.
/// </para>
/// <para>
/// Here rather than in <c>Bobcat.Xunit.Tests</c> because this project already links
/// <see cref="FakeMonitorHost"/> and already drives this host in server mode for
/// <see cref="WarmProjectedRunTests"/>.
/// </para>
/// </remarks>
public class ProjectedEventModelPublishingEndToEndTests
{
    private const string source = "Bobcat-Xunit-Samples";
    private const string sliceFilter = "*SliceSpecs*";

    private static readonly string projectedHost = locate("Bobcat.Xunit.Samples");

    private static string locate(string project)
    {
        var configuration = Path.GetFileName(Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.Name != "src") directory = directory.Parent;

        if (directory is null) throw new InvalidOperationException("Could not locate the src directory.");

        return Path.Combine(
            directory.FullName, project, "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? project + ".exe" : project);
    }

    /// <summary>
    /// The worker environment pointed at <paramref name="host"/>. <c>BOBCAT_MONITOR=1</c> is
    /// explicit for the reason <see cref="WarmProjectedRunTests"/> gives: CI sets it to 0 for the
    /// whole job, and the worker would inherit that.
    /// </summary>
    private static Dictionary<string, string> wiredTo(FakeMonitorHost host)
        => new() { ["BOBCAT_MONITOR_URL"] = host.Url, ["BOBCAT_MONITOR"] = "1" };

    /// <summary>Run the host once, one-shot, over the two slice specs. Returns its output.</summary>
    private static async Task<string> runOnce(IReadOnlyDictionary<string, string> environment)
    {
        File.Exists(projectedHost).ShouldBeTrue($"The host was not built at {projectedHost}");

        var info = new ProcessStartInfo(projectedHost)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(projectedHost)!
        };

        info.ArgumentList.Add("--filter-class");
        info.ArgumentList.Add(sliceFilter);
        foreach (var (name, value) in environment) info.Environment[name] = value;
        info.Environment["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1";
        info.Environment.Remove("BOBCAT_SPEC_CONSOLE");

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var output = await stdout + await stderr;
        process.ExitCode.ShouldBe(0, output);
        return output;
    }

    /// <summary>
    /// Run until the host actually attached to <paramref name="host"/>, up to three times.
    /// </summary>
    /// <remarks>
    /// The host's <c>/api/ping</c> probe has a 250ms ceiling, by Bobcat's rule that an absent console
    /// costs a run next to nothing, and a cold process on a busy CI box can lose that race and run
    /// unattached. That is correct for the host and vacuous for a test about what it sent, so
    /// attachment is observed (the run posted to <c>/api/ingest</c>) rather than assumed.
    /// </remarks>
    private static async Task<string> runAttached(FakeMonitorHost host)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var output = await runOnce(wiredTo(host));
            if (host.Batches.Count > 0) return output;
        }

        throw new ShouldAssertException(
            $"The host never attached to the fake console at {host.Url} in {attempts} runs.");
    }

    [Fact]
    public async Task a_one_shot_run_sends_its_half_exactly_once()
    {
        using var host = new FakeMonitorHost();

        await runAttached(host);

        // Two scenarios ran; the half went once, under the assembly's own source name.
        var push = host.EventModelPushes.ShouldHaveSingleItem();
        push.Source.ShouldBe(source);
        push.Body.ShouldContain("\"name\":\"Bobcat.Xunit.Samples\"");
        push.Body.ShouldContain("\"name\":\"CreditWallet\"");
        push.Body.ShouldContain("\"identity\":\"Wallet/a credit raises the balance\"");
        push.Body.ShouldContain("\"identity\":\"Wallet/a second credit adds to the first\"");
    }

    [Fact]
    public async Task nothing_is_sent_when_no_monitor_is_attached()
    {
        using var host = new FakeMonitorHost();

        // The URL points at a live console, and the kill switch still wins: no probe, no ingest,
        // and no half.
        var environment = wiredTo(host);
        environment["BOBCAT_MONITOR"] = "0";

        await runOnce(environment);

        host.Batches.ShouldBeEmpty();
        host.EventModelPushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_half_naming_a_different_model_is_withheld_and_the_reason_is_printed()
    {
        using var host = new FakeMonitorHost { CurrentEventModelName = "SomeOtherService" };

        var output = await runAttached(host);

        host.EventModelPushes.ShouldBeEmpty();
        output.ShouldContain("did not publish the Event Model half of 'Bobcat.Xunit.Samples'");
        output.ShouldContain("[assembly: EventModelName(\"SomeOtherService\")]");
    }

    /// <summary>
    /// A warm host opens one bracket per run request (#402), and each one sends the half, the way
    /// the Gherkin resident runner attached and published once per command. Discovery opens no
    /// bracket and sends nothing.
    /// </summary>
    [Fact]
    public async Task a_warm_host_sends_the_half_once_per_run_request_and_never_on_discovery()
    {
        using var host = new FakeMonitorHost();

        await using var client = await MtpWorkerClient.Launch(projectedHost, wiredTo(host));

        var discovered = await client.Discover();
        await Task.Delay(500);
        host.EventModelPushes.ShouldBeEmpty("discovery is not a run");

        var slices = discovered.Where(t => t.DisplayName.Contains(".SliceSpecs.")).ToArray();
        slices.Length.ShouldBe(2);

        await client.Run(slices.Select(t => t.Uid).ToArray());
        await client.Run([slices[0].Uid]);

        // Waited on with a deadline rather than read once after a fixed 500ms (bobcat#479): the
        // batches reach the console on the publisher's own pump after Run returns. Waiting for at
        // least what is expected and then settling keeps the exact assertion below strict.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((host.Batches.Count == 0 || host.EventModelPushes.Count < 2) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await Task.Delay(300);

        // The probe runs once per process, so a warm host that lost the 250ms race (see
        // runAttached) stays unattached, and the assertion below would be about nothing.
        host.Batches.ShouldNotBeEmpty("the warm host never attached to the fake console");

        host.EventModelPushes.Select(p => p.Source).ShouldBe([source, source]);
    }
}
