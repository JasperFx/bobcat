using Bobcat.Runtime;
using Wolverine.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.RabbitMQ;

namespace Bobcat.Wolverine.Tests;

public record LeakedWork(Guid ScenarioId);

/// <summary>
/// Records which scenario each message was <em>handled</em> in, which is the whole question: a
/// message cascaded by scenario A and handled during scenario B is the defect.
/// </summary>
public class LeakedWorkHandler
{
    public static Guid CurrentScenario;
    private static readonly List<(Guid SentIn, Guid HandledIn)> _handled = [];

    public static void Reset()
    {
        lock (_handled) _handled.Clear();
    }

    public static IReadOnlyList<(Guid SentIn, Guid HandledIn)> Handled
    {
        get { lock (_handled) return _handled.ToArray(); }
    }

    public static void Handle(LeakedWork message)
    {
        lock (_handled) _handled.Add((message.ScenarioId, CurrentScenario));
    }
}

/// <summary>
/// Issue #282: a per-scenario reset clears the store but not the broker, so one scenario's cascade
/// is delivered inside the next one.
/// </summary>
/// <remarks>
/// <para>
/// Reported against a real Wolverine + Polecat + RabbitMQ suite: a scenario acted, its handler
/// cascaded across the broker, the scenario ended, <c>ResetBetweenScenarios</c> cleared the
/// document store and the envelope tables — and the messages already sitting on RabbitMQ survived
/// it. They arrived inside the <em>next</em> scenario's tracked session, against a store that no
/// longer held what they referred to, failing it with an id belonging to a scenario that had
/// already passed.
/// </para>
/// <para>
/// <b>The broker is not incidental to the test.</b> An in-memory transport has nowhere for a
/// message to survive a reset, which is exactly why every existing sample missed this — the issue
/// says so, and it is the reason these tests take a real RabbitMQ rather than a stub.
/// </para>
/// </remarks>
public class TransportIsolationTests
{
    private static string queueName(string suffix) => $"bobcat_282_{suffix}_{Guid.NewGuid():N}";

    private static HostResource<LeakedWorkHandler> resourceFor(string queue, bool drainTransports)
        => new(
            configure: builder =>
            {
                builder.Services.AddWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<LeakedWorkHandler>();

                    opts.UseRabbitMq(factory =>
                        {
                            factory.HostName = RabbitEnvironment.Host;
                            factory.Port = RabbitEnvironment.Port;
                        })
                        .AutoProvision()
                        .AutoPurgeOnStartup();

                    opts.PublishMessage<LeakedWork>().ToRabbitQueue(queue);
                    opts.ListenToRabbitQueue(queue);
                });
            },
            name: "app",
            reset: drainTransports
                ? host => host.DrainTransportsAsync()
                : _ => Task.CompletedTask);

    /// <summary>
    /// The reported defect, reproduced: a message published in scenario A is handled during
    /// scenario B when the reset does not clear the transport.
    /// </summary>
    [RabbitFact]
    public async Task without_a_transport_drain_a_cascade_leaks_into_the_next_scenario()
    {
        var leaked = await runTwoScenarios(drainTransports: false);

        // Scenario A's message was handled in scenario B — an id from a run that already passed.
        leaked.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The fix: the reset drains what the suite listens to, so nothing crosses the boundary.
    /// </summary>
    [RabbitFact]
    public async Task draining_the_transports_in_the_reset_keeps_the_scenarios_isolated()
    {
        var leaked = await runTwoScenarios(drainTransports: true);

        leaked.ShouldBeEmpty();
    }

    /// <summary>
    /// Scenario A leaves a message <b>on the broker</b> across the scenario boundary, which is
    /// exactly what was reported, and does it deterministically: the listener is stopped first, so
    /// nothing can consume the message before the reset and the test is not racing its own setup.
    /// Then the reset runs, then scenario B starts listening again. Anything handled in B was sent
    /// in A and should not be there.
    /// </summary>
    /// <remarks>
    /// A scenario ending with its cascade still in Wolverine's OUTBOUND buffer — not yet on the
    /// broker — is a different problem and is not what this pins: nothing on the broker means
    /// nothing for a purge to remove. See the note on <c>DrainTransportsAsync</c>.
    /// </remarks>
    private static async Task<IReadOnlyList<(Guid SentIn, Guid HandledIn)>> runTwoScenarios(bool drainTransports)
    {
        LeakedWorkHandler.Reset();

        var queue = queueName(drainTransports ? "drained" : "leaky");
        await using var resource = resourceFor(queue, drainTransports);
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var runtime = resource.Host.Services.GetRequiredService<IWolverineRuntime>();
        var listeners = runtime.Endpoints.ActiveListeners()
            .Select(a => runtime.Endpoints.EndpointFor(a.Uri)!)
            .ToList();
        listeners.ShouldNotBeEmpty("the test needs a real listener to stop, or it proves nothing");

        // ---- scenario A: act, and leave the result on the broker.
        var scenarioA = Guid.NewGuid();
        LeakedWorkHandler.CurrentScenario = scenarioA;
        await suite.ResetAll();
        await resource.BeginScenarioScope();

        foreach (var endpoint in listeners)
        {
            await runtime.Endpoints.StopListenerAsync(endpoint, CancellationToken.None);
        }

        var bus = resource.CurrentServices.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new LeakedWork(scenarioA));

        // Give the send time to actually reach RabbitMQ — the premise of the whole test is that
        // the message IS on the broker when the boundary is crossed.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await resource.EndScenarioScope();

        // ---- the boundary. This is the thing under test.
        await suite.ResetAll();

        // ---- scenario B: listen again. Anything arriving belongs to A.
        var scenarioB = Guid.NewGuid();
        LeakedWorkHandler.CurrentScenario = scenarioB;
        await resource.BeginScenarioScope();

        foreach (var endpoint in listeners)
        {
            await runtime.Endpoints.StartListenerAsync(endpoint, CancellationToken.None);
        }

        // Long enough for a surviving message to be delivered — the leak is a race the test must
        // give every chance to happen, or a green result means nothing.
        await Task.Delay(TimeSpan.FromSeconds(3));
        await resource.EndScenarioScope();

        return LeakedWorkHandler.Handled.Where(h => h.SentIn != h.HandledIn).ToList();
    }
}
