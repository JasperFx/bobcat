using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bobcat.Console.EventModel;
using Bobcat.Monitoring;
using Bobcat.Runtime;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #294, end to end: a <b>real</b> <see cref="BobcatRunner"/> over this assembly's
/// <b>real</b> generated <c>BobcatEventModelSource</c> (from <c>Features/Wallet.feature</c>)
/// publishes its half of the Event Model over real HTTP into a <b>real</b>
/// <see cref="EventModelStore"/>, and the half comes back out of <c>GET /api/event-model</c>
/// folded together with a host half nobody in this process wrote.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the merge #268 built had no producer: outside <c>Bobcat.Console</c> and its own
/// tests nothing in the repository PUT to <c>/api/event-model</c>, so on a real application the
/// merged model existed only if a human pushed both halves by hand — and CritterWatch#1212
/// measured the consequence at 125 of 125 slices reading "no specification bound".
/// </para>
/// <para>
/// What is real here and what is not: the runner, the generated descriptor, the HTTP, the source
/// name, the store, and <c>EventModelDescriptor.Merge</c> are all the shipping article. The only
/// stand-in is ASP.NET's routing — <see cref="FakeConsole"/> dispatches the two Event Model
/// routes onto the same two <see cref="EventModelStore"/> calls <c>EventModelEndpoints</c> makes,
/// because Alba's <c>TestServer</c> (what <c>Bobcat.Console.Specs</c> boots the console on) has no
/// port for an <c>HttpClient</c> to reach. The endpoints' own behaviour — the 400 body, the
/// <c>EventModelChanged</c> broadcast — is covered by <c>Bobcat.Console.Specs</c>'s
/// <c>EventModel.feature</c>.
/// </para>
/// </remarks>
public class SpecEventModelPublishingTests : IDisposable
{
    private readonly string? _previousKillSwitch;
    private readonly string? _previousUrl;
    private readonly string _dataPath;

    public SpecEventModelPublishingTests()
    {
        // CI sets BOBCAT_MONITOR=0 so the spec hosts it collects never publish; without clearing
        // it these would assert against a publisher the kill switch refused to build.
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        _previousUrl = Environment.GetEnvironmentVariable(MonitorPublisher.UrlVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);

        _dataPath = Path.Combine(Path.GetTempPath(), "bobcat-294", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);
        Environment.SetEnvironmentVariable(MonitorPublisher.UrlVariable, _previousUrl);

        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    /// <summary>
    /// What Wolverine's <c>event-model</c> export produces: the slice with its command and its
    /// HANDLER — a type no spec assembly knows — and no <c>Specifications</c> at all, because the
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

    private async Task<SuiteResults> runTheSpecs(FakeConsole console)
    {
        Environment.SetEnvironmentVariable(MonitorPublisher.UrlVariable, console.Url);

        var runner = new BobcatRunner
        {
            // Exactly what the real entry points set — BobcatTestFramework and BobcatRunner.Run.
            PublishToMonitor = true,
            SuppressConsoleOutput = true
        };

        runner.ScanForFeatures(typeof(WalletFixture).Assembly);

        return await runner.RunAll();
    }

    private EventModelDescriptor mergedModel(EventModelStore store)
        => JsonSerializer.Deserialize<EventModelDescriptor>(
               store.Read() ?? throw new ShouldAssertException("GET /api/event-model would answer 404 — nothing is published."),
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
            .Specifications.ShouldBeEmpty("the host half cannot see a spec assembly — that is the whole defect");

        var results = await runTheSpecs(console);

        // The run itself is untouched: publishing is a side channel, never a participant.
        results.CatastrophicFailure.ShouldBeNull();
        results.DiscoveryFailure.ShouldBeNull();
        results.AllScenarios.Count().ShouldBe(2);

        var merged = mergedModel(store);
        merged.Name.ShouldBe("Wallets");

        var credit = merged.Slices.Single(s => s.Name == "CreditWallet");

        // The spec half arrived …
        credit.Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet/Crediting a wallet emits the credited event");

        // … folded into the host's slice rather than replacing it (the handler type is the host's
        // alone, and nothing in this assembly could have published it) …
        credit.HandlerType.ShouldNotBeNull();
        credit.HandlerType.Name.ShouldBe(nameof(WalletHandler));

        // … and the spec half's own slices came with it.
        merged.Slices.Single(s => s.Name == "DebitWallet")
            .Specifications.Select(s => s.Identity)
            .ShouldContain("Wallet/Debiting a wallet emits the debited event");
    }

    /// <summary>
    /// The source name is the spec assembly's, with every character a file name will not take
    /// turned into '-'. Asserted against the store's own file rather than against the publisher,
    /// because verbatim it is <c>EventModelStore.TryStore</c> that refuses it — a dotted source
    /// is a 400 on every single run, and a silent one.
    /// </summary>
    [Fact]
    public async Task the_half_lands_under_a_stable_source_named_for_the_spec_assembly()
    {
        var store = new EventModelStore(_dataPath);
        using var console = new FakeConsole(store);

        await runTheSpecs(console);
        await runTheSpecs(console);

        Directory.EnumerateFiles(_dataPath, "event-model*.json")
            .Select(Path.GetFileName)
            .ShouldBe(["event-model.Bobcat-Console-Tests.json"]);

        // A re-run REPLACES that source. Two runs of a two-slice assembly is still two slices,
        // each still carrying exactly the one specification its feature declares.
        var merged = mergedModel(store);
        merged.Slices.Count.ShouldBe(2);
        merged.Slices.Single(s => s.Name == "CreditWallet").Specifications.Count.ShouldBe(1);
    }

    /// <summary>
    /// The contract issue #294 says nothing checks. <c>GET /api/event-model</c> merges only the
    /// sources naming the CURRENT model, and the current name is whatever was pushed last — so a
    /// spec half naming a different model would not join the host's half, it would HIDE it. That
    /// is a worse outcome than not publishing, so the runner does not publish. (The message it
    /// prints instead is pinned in <c>Bobcat.Tests</c>; here the assertion is that the other half
    /// survives.)
    /// </summary>
    [Fact]
    public async Task a_spec_half_naming_another_model_is_withheld_rather_than_hiding_the_host_half()
    {
        var store = new EventModelStore(_dataPath);
        using var console = new FakeConsole(store);

        // This assembly declares [assembly: EventModelName("Wallets")]; the console is serving
        // something else entirely — two models, not two halves of one.
        store.TryStore(hostHalf("Payments"), EventModelStore.DefaultSource).ShouldBeNull();

        await runTheSpecs(console);

        // Not vacuous: the console WAS reachable — it took this run's progress events — so
        // publishing nothing was a decision rather than an absence.
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
    /// The two Event Model routes over a real <see cref="EventModelStore"/>, plus the
    /// <c>/api/ping</c> a <see cref="MonitorPublisher"/> probes and the <c>/api/ingest</c> its
    /// event pump posts to. The dispatch mirrors <c>EventModelEndpoints</c> one call at a time;
    /// see this class's remarks for why the real endpoints are not used.
    /// </summary>
    private sealed class FakeConsole : IDisposable
    {
        private readonly EventModelStore _store;
        private readonly HttpListener _listener;
        private readonly List<string> _ingested = new();

        public string Url { get; }

        /// <summary>
        /// Every <c>/api/ingest</c> batch this console took. Read by the withholding test, which
        /// would otherwise pass over a console nobody could reach.
        /// </summary>
        public IReadOnlyList<string> Ingested
        {
            get { lock (_ingested) return _ingested.ToArray(); }
        }

        public FakeConsole(EventModelStore store)
        {
            _store = store;

            // freePort() binds and releases a TcpListener and HttpListener binds it again a
            // moment later; on a busy box another process can take it in between. Retry with a
            // fresh port — the same narrow race Bobcat.Tests' FakeMonitorHost documents.
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
                        await answer(context, _store.Read() is { } json ? 200 : 404, _store.Read());
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
                        // /api/ping — the probe, which must succeed or the run never gets as far
                        // as publishing a model at all.
                        await answer(context, 200, null);
                    }
                }
            }
            catch
            {
                // Listener disposed — test over.
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

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _listener.Stop(); } catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
        }
    }
}
