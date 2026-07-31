using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Monitor.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Util;

namespace Bobcat.Monitor.Tests;

/// <summary>
/// The server-side batching seam: ingested events coalesce into one
/// <see cref="BatchedWebSocketPayload"/> per flush tick instead of one SignalR frame per
/// event. DrainOnce is the deterministic unit under test — the background timer is just a
/// loop around it.
/// </summary>
public class SignalRBatchingTests
{
    private static readonly Guid runId = Guid.NewGuid();

    private static SignalRBatchAccumulator newAccumulator()
        => new(new ServiceCollection().BuildServiceProvider(),
            NullLogger<SignalRBatchAccumulator>.Instance);

    [Fact]
    public void draining_an_empty_queue_yields_nothing_so_no_empty_frames_ship()
    {
        newAccumulator().DrainOnce().ShouldBeNull();
    }

    [Fact]
    public void drain_folds_everything_queued_into_one_envelope_in_arrival_order()
    {
        var accumulator = newAccumulator();

        var started = new RunStarted(runId, "Demo", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 2);
        var step = new StepStarted(runId, "Calc/adds", "s1", "Given", "a calculator");
        var heartbeat = new RunHeartbeat(runId, DateTimeOffset.UtcNow);

        // Two POSTs landing inside one flush window fold into a single envelope.
        accumulator.Enqueue([started, step]);
        accumulator.Enqueue([heartbeat]);

        var batch = accumulator.DrainOnce().ShouldNotBeNull();
        batch.Items.Select(i => i.Type).ShouldBe(["run_started", "step_started", "run_heartbeat"]);
        batch.Items.Select(i => i.Data).ShouldBe([started, step, heartbeat]);

        // The drain emptied the queue — the next tick ships nothing.
        accumulator.DrainOnce().ShouldBeNull();
    }

    [Fact]
    public void every_item_wire_name_matches_the_events_json_discriminator()
    {
        // The frontend re-dispatches each unwrapped item by its Type string, so the wire name
        // Wolverine derives (WebSocketMessage naming: PascalCase → snake_case) must equal the
        // STJ discriminator MonitorEvents.cs pins — for every event type, present and future.
        var derived = typeof(MonitorEvent).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToArray();

        derived.ShouldNotBeEmpty();
        foreach (var attribute in derived)
        {
            attribute.DerivedType.ToMessageTypeName().ShouldBe((string)attribute.TypeDiscriminator!);
        }

        // And the envelope itself is spelled the way relayToStore's unwrap case expects.
        typeof(BatchedWebSocketPayload).ToMessageTypeName().ShouldBe("batched_web_socket_payload");
    }

    [Fact]
    public void the_serialized_envelope_is_the_shape_the_frontend_unwraps()
    {
        var accumulator = newAccumulator();
        accumulator.Enqueue([new RunStarted(runId, "Demo", "/repo", "main", "in-process", DateTimeOffset.UtcNow, 2)]);

        // Wolverine.SignalR serializes the outgoing message with web defaults into the
        // CloudEvents `data` field (pinned by relayToStore.test.ts); this is that payload.
        var json = JsonSerializer.Serialize(accumulator.DrainOnce()!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        var item = document.RootElement.GetProperty("items")[0];
        item.GetProperty("type").GetString().ShouldBe("run_started");
        item.GetProperty("data").GetProperty("runId").GetGuid().ShouldBe(runId);
        // Data is declared as the polymorphic base, so the inner discriminator survives too —
        // an unwrapped item is byte-compatible with a per-event envelope.
        item.GetProperty("data").GetProperty("type").GetString().ShouldBe("run_started");
    }
}
