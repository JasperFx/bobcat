using System.Text.Json;
using Bobcat.Residency;
using Shouldly;

namespace Bobcat.Tests.Residency;

/// <summary>
/// The resident runner wire as a contract (issue #390). The receiving side has no assembly
/// reference to warn it, exactly like <c>MonitorEvents</c>, so these tests are the only thing
/// standing between a renamed property here and a console that silently stops understanding a
/// runner.
/// </summary>
public class RunnerWireContractTests
{
    private static JsonElement parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // --- The envelope.

    [Fact]
    public void an_event_is_a_structured_mode_cloudevent()
    {
        var root = parse(CloudEvent
            .From(CloudEvent.SourceFor("r1"), RunnerWire.RegisteredType, new { hello = "world" })
            .ToJson());

        root.GetProperty("specversion").GetString().ShouldBe("1.0");
        root.GetProperty("source").GetString().ShouldBe("bobcat/runner/r1");
        root.GetProperty("type").GetString().ShouldBe("bobcat.runner.registered");
        root.GetProperty("datacontenttype").GetString().ShouldBe("application/json");
        root.GetProperty("data").GetProperty("hello").GetString().ShouldBe("world");
        root.TryGetProperty("time", out _).ShouldBeTrue();
    }

    [Fact]
    public void every_id_is_a_guid()
    {
        // Not a style choice: the receiving side's mapper replaces an id it cannot read as a GUID
        // with a freshly minted one, which would silently break a correlation the sender relied on.
        var id = CloudEvent.From("bobcat/runner/r1", RunnerWire.RegisteredType, new { }).Id;

        Guid.TryParse(id, out _).ShouldBeTrue(id);
    }

    [Fact]
    public void the_media_type_is_the_cloudevents_one()
    {
        CloudEvent.MediaType.ShouldBe("application/cloudevents+json");
    }

    [Fact]
    public void an_envelope_round_trips()
    {
        var sent = CloudEvent.From(
            CloudEvent.SourceFor("r1"),
            RunnerWire.RunCommandType,
            new RunCommand("c1", ["Orders/places an order"], RunnerWire.ColdMode));

        var read = CloudEvent.FromJson(sent.ToJson()).ShouldNotBeNull();

        read.Id.ShouldBe(sent.Id);
        read.Type.ShouldBe(RunnerWire.RunCommandType);
        read.DataAs<RunCommand>().ShouldNotBeNull().Specs.ShouldBe(["Orders/places an order"]);
    }

    [Fact]
    public void a_payload_that_does_not_read_as_the_expected_shape_is_null_rather_than_an_exception()
    {
        // A runner reads what a monitor sent and must treat anything it cannot understand as
        // something to ignore, not something to die of.
        var @event = CloudEvent.FromJson(
            """{"specversion":"1.0","id":"x","source":"stoat","type":"stoat.runner.command.run","data":"not an object"}""");

        @event.ShouldNotBeNull().DataAs<RunCommand>().ShouldBeNull();
    }

    [Fact]
    public void something_that_is_not_json_at_all_reads_as_no_event()
    {
        CloudEvent.FromJson("<html>a proxy error page</html>").ShouldBeNull();
    }

    // --- The type and route vocabulary.

    [Fact]
    public void the_types_are_the_ones_the_other_side_subscribes_to()
    {
        RunnerWire.RegisteredType.ShouldBe("bobcat.runner.registered");
        RunnerWire.AcknowledgedType.ShouldBe("bobcat.runner.command.acknowledged");
        RunnerWire.RunCommandType.ShouldBe("stoat.runner.command.run");
        RunnerWire.RestartCommandType.ShouldBe("stoat.runner.command.restart");
    }

    [Fact]
    public void the_routes_are_the_ones_the_other_side_serves()
    {
        RunnerWire.EventsRoute.ShouldBe("/api/runners/events");
        RunnerWire.CommandsRoute("r1").ShouldBe("/api/runners/r1/commands");
    }

    [Fact]
    public void a_runner_id_is_escaped_into_its_route()
    {
        RunnerWire.CommandsRoute("a runner/one").ShouldBe("/api/runners/a%20runner%2Fone/commands");
    }

    // --- The payloads.

    [Fact]
    public void a_registration_is_camel_case_on_the_wire()
    {
        // The receiving side is a .NET web host whose default is camelCase, so these payloads are
        // written the way it will read them.
        var root = parse(JsonSerializer.Serialize(
            new RunnerRegistration("r1", "/repo", "main", "Orders.Specs", "gherkin",
                ["cold"], ["Orders/places an order"]),
            RunnerWire.Json));

        root.GetProperty("runnerId").GetString().ShouldBe("r1");
        root.GetProperty("repository").GetString().ShouldBe("/repo");
        root.GetProperty("branch").GetString().ShouldBe("main");
        root.GetProperty("suite").GetString().ShouldBe("Orders.Specs");
        root.GetProperty("lane").GetString().ShouldBe("gherkin");
        root.GetProperty("modes").EnumerateArray().Select(x => x.GetString()).ShouldBe(["cold"]);
        root.GetProperty("specs").EnumerateArray().Select(x => x.GetString())
            .ShouldBe(["Orders/places an order"]);
    }

    [Fact]
    public void an_acknowledgement_carries_the_command_it_answers_and_a_reason_when_it_refuses()
    {
        var root = parse(JsonSerializer.Serialize(
            new RunnerAcknowledgement("r1", "c1", false, "busy"), RunnerWire.Json));

        root.GetProperty("runnerId").GetString().ShouldBe("r1");
        root.GetProperty("commandId").GetString().ShouldBe("c1");
        root.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        root.GetProperty("reason").GetString().ShouldBe("busy");
    }

    [Fact]
    public void a_command_with_no_mode_is_cold()
    {
        // Cold is the default because a fresh filtered run always runs the current code, so an
        // older monitor that sends no mode gets the safe one.
        new RunCommand("c1", ["Orders/places an order"]).ResolvedMode.ShouldBe("cold");
        new RunCommand("c1", ["Orders/places an order"], "  ").ResolvedMode.ShouldBe("cold");
        new RunCommand("c1", ["Orders/places an order"], "warm").ResolvedMode.ShouldBe("warm");
    }

    [Fact]
    public void a_command_reads_from_the_json_a_monitor_sends()
    {
        var command = parse("""{"commandId":"c1","specs":["Orders/places an order"],"mode":"warm"}""")
            .Deserialize<RunCommand>(RunnerWire.Json)
            .ShouldNotBeNull();

        command.CommandId.ShouldBe("c1");
        command.Specs.ShouldBe(["Orders/places an order"]);
        command.ResolvedMode.ShouldBe("warm");
    }
}
