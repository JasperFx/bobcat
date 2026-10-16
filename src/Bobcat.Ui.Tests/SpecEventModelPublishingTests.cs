using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bobcat.Ui.EventModel;
using Bobcat.Monitoring;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// Issue #294, end to end: a <b>real</b> projected run of this assembly's <see cref="WalletSpecs"/> publishes
/// its <b>real</b> generated <c>BobcatEventModelSource</c> over real HTTP into a <b>real</b>
/// <see cref="EventModelStore"/>, and the half comes back out of <c>Read()</c> -- what
/// <c>GET /api/event-model</c> serves -- folded together with a host half nobody in this process wrote.
/// </summary>
/// <remarks>
/// <para>
/// Before #294 the merge #268 built had no producer: on a real application the merged model existed only
/// if a human pushed both halves by hand, and CritterWatch#1212 measured the consequence at 125 of 125
/// slices reading "no specification bound".
/// </para>
/// <para>
/// The producer was the Gherkin runner's until Bobcat 0.50.0 deleted that lane. From 0.50.0 it is the
/// projected lane's run bracket (<c>SpecEventModelPublisher</c>, called from <c>MarkerStepRun.openRun</c>,
/// bobcat#488/#491), once per bracket. The bracket resolves its console once per PROCESS, so a test cannot
/// point this process's bracket at a fake console after the fact -- the run under test is a child
/// process: this same assembly, run with <c>--filter-class</c> down to <see cref="WalletSpecs"/>, with
/// <c>BOBCAT_MONITOR_URL</c> aimed at <see cref="FakeConsole"/>. The entry assembly of that child is this
/// one, which is where the bracket looks for a spec half.
/// </para>
/// <para>
/// What is real here and what is not: the run, the generated descriptor, the HTTP, the source name, the
/// store, and <c>EventModelDescriptor.Merge</c> are all the shipping article. The only stand-in is
/// ASP.NET's routing -- <see cref="FakeConsole"/> dispatches the two Event Model routes onto the same two
/// <see cref="EventModelStore"/> calls <c>EventModelEndpoints</c> makes, because Alba's <c>TestServer</c>
/// (what <c>Bobcat.Ui.Specs</c> boots the console on) has no port for another process to reach.
/// </para>
/// </remarks>
public class SpecEventModelPublishingTests : IDisposable
{
    /// <summary>The source Bobcat's publisher names this assembly's half: its name, dots turned into '-'.</summary>
    private const string SpecSource = "Bobcat-Ui-Tests";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "bobcat-294", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    /// <summary>
    /// What Wolverine's <c>event-model</c> export produces: the slice with its command and its
    /// HANDLER -- a type no spec assembly knows -- and no <c>Specifications</c> at all, because the
    /// host cannot see the spec assembly that declares them.
    /// </summary>
    private static string hostHalf(string modelName)
    {
        var slice = new EventModelSliceDescriptor(
            "CreditWallet", null, null,
            TypeDescriptor.For(typeof(CreditWallet)),
            TypeDescriptor.For(typeof(WalletHandler)),
            [], [], []);

        return JsonSerializer.Serialize(
            new EventModelDescriptor(modelName, [slice]).WithProvenance(EventModelProvenance.Derived),
            EventModelStore.Wire);
    }

    /// <summary>This assembly's generated half, as the publisher put it on the wire.</summary>
    private static async Task<string> specHalf()
    {
        IEventModelDefinitionSource source = Bobcat.Generated.EventModel.BobcatEventModelSource.Instance;
        await using var services = new ServiceCollection().BuildServiceProvider();
        var descriptor = await source.TryCreateAsync(services, CancellationToken.None)
                         ?? throw new ShouldAssertException("The generated source declared no Event Model half.");
        return JsonSerializer.Serialize(descriptor.WithProvenance(source.Provenance), EventModelStore.Wire);
    }

    /// <summary>
    /// Run this assembly's <see cref="WalletSpecs"/> in a child process attached to
    /// <paramref name="console"/> -- again, up to three runs in all, when a run never attached to it.
    /// </summary>
    /// <remarks>
    /// The bracket's <c>/api/ping</c> probe has a 250ms ceiling, by Bobcat's own rule that an absent
    /// console costs a run next to nothing. A cold child on a busy CI box can lose that race, and the run
    /// then goes ahead unattached: correct for Bobcat, and vacuous for every test here. Whether a run
    /// attached is observed, not timed: an attached bracket asks the console which model it serves
    /// before it publishes anything, so a run that never asked is a run that never saw the console.
    /// </remarks>
    private static async Task runTheSpecs(FakeConsole console)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var reads = console.ModelReads;

            var exitCode = await runWalletSpecsInAChild(console.Url);
            exitCode.ShouldBe(0, "the child run's specs failed -- publishing is a side channel and never a participant");

            if (console.ModelReads > reads) return;
        }

        throw new ShouldAssertException(
            $"The projected run never attached to the fake console at {console.Url} in {attempts} runs: its /api/ping probe lost every time.");
    }

    private static async Task<int> runWalletSpecsInAChild(string consoleUrl)
    {
        var assembly = typeof(WalletSpecs).Assembly.Location;
        var results = Path.Combine(Path.GetTempPath(), "bobcat-294-results", Guid.NewGuid().ToString("N"));

        var start = new ProcessStartInfo(dotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assembly)!
        };

        foreach (var arg in new[]
                 {
                     "exec", assembly,
                     "--filter-class", typeof(WalletSpecs).FullName!,
                     "--results-directory", results
                 })
        {
            start.ArgumentList.Add(arg);
        }

        // CI sets BOBCAT_MONITOR=0 so the suites it runs never publish; the child has to be allowed to.
        start.Environment.Remove(MonitorPublisher.KillSwitchVariable);
        start.Environment[MonitorPublisher.UrlVariable] = consoleUrl;

        // The local spec report is noise in a child nobody reads.
        start.Environment["BOBCAT_SPEC_CONSOLE"] = "0";

        using var child = Process.Start(start)
                          ?? throw new ShouldAssertException($"Could not start {start.FileName} for the child run.");

        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw new ShouldAssertException($"The child run of {nameof(WalletSpecs)} did not finish in a minute.\n{await stdout}\n{await stderr}");
        }

        try { Directory.Delete(results, recursive: true); } catch { }

        if (child.ExitCode != 0)
        {
            TestContext.Current.SendDiagnosticMessage($"child stdout:\n{await stdout}\nchild stderr:\n{await stderr}");
        }

        return child.ExitCode;
    }

    /// <summary>
    /// The <c>dotnet</c> muxer this process runs under: the SDK hands it to the processes it starts as
    /// <c>DOTNET_HOST_PATH</c>, and a test host started some other way is usually that muxer itself.
    /// </summary>
    private static string dotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;

        var process = Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process) == "dotnet") return process;

        return "dotnet";
    }

    private static EventModelDescriptor mergedModel(EventModelStore store)
        => JsonSerializer.Deserialize<EventModelDescriptor>(
               store.Read() ?? throw new ShouldAssertException("GET /api/event-model would answer 404 -- nothing is published."),
               EventModelStore.Wire)!;

    [Fact]
    public async Task a_run_publishes_the_spec_half_and_it_joins_the_host_half_in_the_merge()
    {
        var store = new EventModelStore(_dataPath);
        using var console = new FakeConsole(store);

        // The host half arrives the way it does today: Wolverine's export, pushed by a CI step or
        // `bobcat watch-event-model`, under the default source.
        store.TryStore(hostHalf("Wallets"), EventModelStore.DefaultSource).ShouldBeNull();

        mergedModel(store).Slices.Single(s => s.Name == "CreditWallet")
            .Specifications.ShouldBeEmpty("the host half cannot see a spec assembly -- that is the whole defect");

        await runTheSpecs(console);

        // It landed under the source Bobcat names this assembly: its name, dots turned into '-'.
        Directory.EnumerateFiles(_dataPath, "event-model*.json")
            .Select(Path.GetFileName)
            .ShouldBe(["event-model.json", "event-model.Bobcat-Ui-Tests.json"], ignoreOrder: true);

        var merged = mergedModel(store);
        merged.Name.ShouldBe("Wallets");

        var credit = merged.Slices.Single(s => s.Name == "CreditWallet");

        // The spec half arrived ...
        credit.Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet/Crediting a wallet emits the credited event");

        // ... folded into the host's slice rather than replacing it (the handler type is the host's
        // alone, and nothing in this assembly could have published it) ...
        credit.HandlerType.ShouldNotBeNull();
        credit.HandlerType.Name.ShouldBe(nameof(WalletHandler));

        // ... with the roles the projected specs stamped ...
        credit.CommandType!.Name.ShouldBe(nameof(CreditWallet));
        credit.EmittedEvents.Select(e => e.Name).ShouldContain(nameof(WalletCredited));
        credit.Domain.ShouldBe("Wallets");
        credit.TriggerLabel.ShouldBe("a teller working the counter");

        // ... and the spec half's own slices came with it.
        merged.Slices.Single(s => s.Name == "DebitWallet")
            .Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet/Debiting a wallet emits the debited event");
    }

    /// <summary>
    /// The half lands under one stable, dotless source -- verbatim, a dotted assembly name is refused by
    /// <c>EventModelStore.TryStore</c> -- and a re-publish REPLACES that source rather than accumulating.
    /// </summary>
    [Fact]
    public async Task the_half_lands_under_a_stable_source_and_a_republish_replaces_it()
    {
        var store = new EventModelStore(_dataPath);

        store.TryStore(await specHalf(), SpecSource).ShouldBeNull();
        store.TryStore(await specHalf(), SpecSource).ShouldBeNull();

        Directory.EnumerateFiles(_dataPath, "event-model*.json")
            .Select(Path.GetFileName)
            .ShouldBe(["event-model.Bobcat-Ui-Tests.json"]);

        // Two publishes of a two-slice assembly is still two slices, each still carrying exactly the
        // one specification its spec declares.
        var merged = mergedModel(store);
        merged.Slices.Count.ShouldBe(2);
        merged.Slices.Single(s => s.Name == "CreditWallet").Specifications.Count.ShouldBe(1);
    }

    /// <summary>
    /// The contract issue #294 says nothing checks. <c>GET /api/event-model</c> merges only the sources
    /// naming the CURRENT model, and the current name is whatever was pushed last -- so a spec half naming a
    /// different model would not join the host's half, it would HIDE it. That is a worse outcome than not
    /// publishing, so the bracket does not publish. (The notice it writes instead is pinned in
    /// <c>Bobcat.Tests</c>; here the assertion is that the other half survives.)
    /// </summary>
    [Fact]
    public async Task a_spec_half_naming_another_model_is_withheld_rather_than_hiding_the_host_half()
    {
        var store = new EventModelStore(_dataPath);
        using var console = new FakeConsole(store);

        // This assembly declares [assembly: EventModelName("Wallets")]; the console is serving
        // something else entirely -- two models, not two halves of one.
        store.TryStore(hostHalf("Payments"), EventModelStore.DefaultSource).ShouldBeNull();

        // Not vacuous: runTheSpecs returns only once a run asked the console which model it serves,
        // which an attached bracket does right before it publishes -- so publishing nothing was a
        // decision rather than an absence. The child has exited by then, and its exit drains the run's
        // progress events, so they have landed too.
        await runTheSpecs(console);
        console.Ingested.ShouldNotBeEmpty();

        Directory.EnumerateFiles(_dataPath, "event-model*.json")
            .Select(Path.GetFileName)
            .ShouldBe(["event-model.json"]);

        var merged = mergedModel(store);
        merged.Name.ShouldBe("Payments");
        merged.Slices.Single(s => s.Name == "CreditWallet").HandlerType.ShouldNotBeNull();
    }

    /// <summary>A handler type the host half names and no spec assembly could know about.</summary>
    public static class WalletHandler
    {
    }

    /// <summary>
    /// The two Event Model routes over a real <see cref="EventModelStore"/>, plus the <c>/api/ping</c> a
    /// <see cref="MonitorPublisher"/> probes and the <c>/api/ingest</c> its event pump posts to. The
    /// dispatch mirrors <c>EventModelEndpoints</c> one call at a time; see this class's remarks for why
    /// the real endpoints are not used.
    /// </summary>
    private sealed class FakeConsole : IDisposable
    {
        private readonly EventModelStore _store;
        private readonly HttpListener _listener;
        private readonly List<string> _ingested = new();
        private int _modelReads;
        private bool _disposed;

        public string Url { get; }

        /// <summary>
        /// Every <c>/api/ingest</c> batch this console took. Read by the withholding test, which would
        /// otherwise pass over a console nobody could reach.
        /// </summary>
        public IReadOnlyList<string> Ingested
        {
            get { lock (_ingested) return _ingested.ToArray(); }
        }

        /// <summary>
        /// How many times a run has asked which model this console serves. An attached bracket always
        /// asks before it publishes, so this is how <c>runTheSpecs</c> tells a run that saw the console
        /// from one whose probe timed out.
        /// </summary>
        public int ModelReads => Volatile.Read(ref _modelReads);

        public FakeConsole(EventModelStore store)
        {
            _store = store;

            // freePort() binds and releases a TcpListener and HttpListener binds it again a moment
            // later; on a busy box another process can take it in between. Retry with a fresh port.
            HttpListenerException? last = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = freePort();
                Url = $"http://127.0.0.1:{port}";
                _listener = new HttpListener();
                _listener.Prefixes.Add($"{Url}/");
                try
                {
                    _listener.Start();
                    _ = Task.Run(loop);
                    return;
                }
                catch (HttpListenerException e)
                {
                    last = e;
                    ((IDisposable)_listener).Dispose();
                }
            }

            throw new InvalidOperationException("Could not bind a loopback port for the fake console after 5 attempts.", last);
        }

        private async Task loop()
        {
            try
            {
                while (_listener.IsListening)
                {
                    var context = await _listener.GetContextAsync();
                    var path = context.Request.Url?.AbsolutePath ?? "";
                    var method = context.Request.HttpMethod;

                    if (path == "/api/event-model" && method == "GET")
                    {
                        Interlocked.Increment(ref _modelReads);
                        var json = _store.Read();
                        await answer(context, json is null ? 404 : 200, json);
                    }
                    else if (path.StartsWith("/api/event-model/") && method == "PUT")
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        var body = await reader.ReadToEndAsync();
                        var source = Uri.UnescapeDataString(path["/api/event-model/".Length..]);

                        var failure = _store.TryStore(body, source);
                        await answer(
                            context,
                            failure is null ? 204 : 400,
                            failure is null ? null : $"Not an EventModelDescriptor: {failure}");
                    }
                    else if (path == "/api/ingest")
                    {
                        using var reader = new StreamReader(context.Request.InputStream);
                        var batch = await reader.ReadToEndAsync();
                        lock (_ingested) _ingested.Add(batch);
                        await answer(context, 202, null);
                    }
                    else
                    {
                        // /api/ping -- the probe, which must succeed or the run never gets as far as
                        // publishing a model at all.
                        await answer(context, 200, null);
                    }
                }
            }
            catch
            {
                // Listener disposed -- test over.
            }
        }

        private static async Task answer(HttpListenerContext context, int status, string? body)
        {
            context.Response.StatusCode = status;
            if (body is not null)
            {
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
            }

            context.Response.Close();
        }

        private static int freePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _listener.Stop(); } catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
        }
    }
}
