using Bobcat.Residency;
using Shouldly;

namespace Bobcat.Tests.Residency;

/// <summary>
/// Issue #390's hand-rolled <c>text/event-stream</c> reader, against canned streams —
/// <c>System.Net.ServerSentEvents</c> is in the box on net10.0 only, and Bobcat also targets
/// net9.0.
/// </summary>
public class ServerSentEventsTests
{
    private static async Task<List<ServerSentEvent>> read(string stream)
    {
        using var reader = new StringReader(stream.Replace("\r\n", "\n"));

        var events = new List<ServerSentEvent>();
        await foreach (var sent in ServerSentEvents.Read(reader)) events.Add(sent);

        return events;
    }

    [Fact]
    public async Task a_blank_line_dispatches_an_event()
    {
        var events = await read("id: 1\nevent: stoat.runner.command.run\ndata: {\"commandId\":\"c1\"}\n\n");

        var one = events.ShouldHaveSingleItem();
        one.Id.ShouldBe("1");
        one.Type.ShouldBe("stoat.runner.command.run");
        one.Data.ShouldBe("{\"commandId\":\"c1\"}");
    }

    [Fact]
    public async Task several_events_arrive_in_order()
    {
        var events = await read("event: a\ndata: 1\n\nevent: b\ndata: 2\n\n");

        events.Select(e => (e.Type, e.Data)).ShouldBe([("a", "1"), ("b", "2")]);
    }

    [Fact]
    public async Task comments_are_skipped()
    {
        // Which is how a server holds a connection open through a proxy, so a reader that choked
        // on one would drop a stream that was working.
        var events = await read(": connected\n\nevent: a\ndata: 1\n\n: still here\n\n");

        events.ShouldHaveSingleItem().Data.ShouldBe("1");
    }

    [Fact]
    public async Task repeated_data_lines_are_joined_with_newlines()
    {
        (await read("data: {\ndata:   \"commandId\": \"c1\"\ndata: }\n\n"))
            .ShouldHaveSingleItem()
            .Data.ShouldBe("{\n  \"commandId\": \"c1\"\n}");
    }

    [Fact]
    public async Task exactly_one_space_after_the_colon_is_dropped()
    {
        (await read("data:  padded\n\n")).ShouldHaveSingleItem().Data.ShouldBe(" padded");
        (await read("data:tight\n\n")).ShouldHaveSingleItem().Data.ShouldBe("tight");
    }

    [Fact]
    public async Task a_field_with_no_value_at_all_is_legal()
    {
        // "data:" is how a blank data line is written, and indexing past the colon is the obvious
        // way to crash on it.
        (await read("data:\n\n")).ShouldHaveSingleItem().Data.ShouldBe("");
    }

    [Fact]
    public async Task an_event_with_no_event_field_is_the_specs_default_name()
    {
        (await read("data: 1\n\n")).ShouldHaveSingleItem().Type.ShouldBe("message");
    }

    [Fact]
    public async Task an_id_persists_across_events_that_omit_it()
    {
        // The spec's rule, and it matters: a stream that sends its position once and then omits it
        // still has to resume from the latest one seen.
        var events = await read("id: 7\nevent: a\ndata: 1\n\nevent: b\ndata: 2\n\n");

        events.Select(e => e.Id).ShouldBe(["7", "7"]);
    }

    [Fact]
    public async Task a_trailing_event_with_no_blank_line_is_still_dispatched()
    {
        // A server that wrote one event and closed cleanly must not lose it.
        (await read("event: a\ndata: 1")).ShouldHaveSingleItem().Data.ShouldBe("1");
    }

    [Fact]
    public async Task retry_is_read_and_dropped()
    {
        // Deliberately ignored: the reconnect delay is the runner's own backoff, so a monitor
        // cannot make a runner reconnect in a tight loop.
        var events = await read("retry: 1\nevent: a\ndata: 1\n\n");

        events.ShouldHaveSingleItem().Data.ShouldBe("1");
    }

    [Fact]
    public async Task an_unknown_field_does_not_disturb_the_event()
    {
        (await read("whatever: x\nevent: a\ndata: 1\n\n")).ShouldHaveSingleItem().Data.ShouldBe("1");
    }

    [Fact]
    public async Task a_stream_of_nothing_but_comments_yields_nothing()
    {
        (await read(": one\n: two\n\n")).ShouldBeEmpty();
    }
}
