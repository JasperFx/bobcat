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

    // --- Issue #397: the parent owns the runner's identity.

    [Fact]
    public void a_handed_runner_id_is_the_runner_id()
    {
        // A resident runner lives under a watch and is relaunched on every source change, so a
        // minted id per start meant a new runner per rebuild: a command pressed while the runner
        // was rebuilding waits on an id that never comes back, and the monitor's picker fills with
        // dead runners. A parent that derives one stable id from the checkout fixes all of it —
        // provided the runner uses what it was handed.
        var previous = Environment.GetEnvironmentVariable(ResidentRunnerOptions.IdVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ResidentRunnerOptions.IdVariable, "runner-for-this-checkout");

            new ResidentRunnerOptions().RunnerId.ShouldBe("runner-for-this-checkout");

            // Explicit still wins, so a caller constructing options in code is unaffected by a
            // variable something else in the environment set.
            new ResidentRunnerOptions { RunnerId = "mine" }.RunnerId.ShouldBe("mine");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ResidentRunnerOptions.IdVariable, previous);
        }
    }

    [Fact]
    public void a_runner_nobody_named_still_has_an_addressable_id()
    {
        var previous = Environment.GetEnvironmentVariable(ResidentRunnerOptions.IdVariable);
        try
        {
            Environment.SetEnvironmentVariable(ResidentRunnerOptions.IdVariable, null);

            Guid.TryParse(new ResidentRunnerOptions().RunnerId, out _).ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ResidentRunnerOptions.IdVariable, previous);
        }
    }

    [Fact]
    public void the_runner_id_variable_is_the_spelling_a_parent_sets()
    {
        ResidentRunnerOptions.IdVariable.ShouldBe("BOBCAT_RUNNER_ID");
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
            new RunnerAcknowledgement(
                "r1", "c1", false, "this runner is already running a command", RunnerRefusal.Busy),
            RunnerWire.Json));

        root.GetProperty("runnerId").GetString().ShouldBe("r1");
        root.GetProperty("commandId").GetString().ShouldBe("c1");
        root.GetProperty("accepted").GetBoolean().ShouldBeFalse();
        root.GetProperty("reason").GetString().ShouldBe("this runner is already running a command");
        root.GetProperty("refusal").GetString().ShouldBe("busy");
    }

    [Fact]
    public void the_refusal_words_are_the_ones_a_monitor_switches_on()
    {
        // Issue #400. Stoat owns the command queue and has to tell "not now" from "not this": with
        // only the reason to go on it matched the sentence "already running a command", so a
        // rewording here would quietly turn every busy into a hard rejection a person sees as a
        // failed button. These four strings are the contract that replaced that.
        RunnerRefusal.Busy.ShouldBe("busy");
        RunnerRefusal.UnknownSpec.ShouldBe("unknown-spec");
        RunnerRefusal.UnsupportedMode.ShouldBe("unsupported-mode");
        RunnerRefusal.Empty.ShouldBe("empty");
    }

    [Fact]
    public void an_acknowledgement_from_a_monitor_that_predates_the_refusal_field_still_reads()
    {
        // Additive and trailing, which is the only kind of change this wire can take: the
        // receiving side has no assembly reference to tell it a field appeared.
        var ack = parse("""{"runnerId":"r1","commandId":"c1","accepted":false,"reason":"busy"}""")
            .Deserialize<RunnerAcknowledgement>(RunnerWire.Json)
            .ShouldNotBeNull();

        ack.Reason.ShouldBe("busy");
        ack.Refusal.ShouldBeNull();
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
