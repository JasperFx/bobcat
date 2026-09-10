using Bobcat.Engine;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #259: <c>Given {event} occurred</c> takes its fields either way round — a one-row
/// horizontal table like every other Bobcat table, or a vertical <c>| field | value |</c> table,
/// one field per row, for an event too wide to read across.
/// </summary>
/// <remarks>
/// The orientation is decided from the header alone, per step, so no scenario has to commit to one
/// shape. The header is the whole signal, which is why an event with properties literally named
/// <c>Field</c> and <c>Value</c> would be misread as vertical — see the last test.
/// </remarks>
public class ArrangedEventTableTests
{
    private record Proposed(Guid OwnerId, DateTimeOffset ProposedFor);

    private static StepTable table(IReadOnlyList<string> headers, params IReadOnlyList<string>[] rows)
        => new(headers, rows);

    [Fact]
    public void no_table_arranges_a_field_less_event()
    {
        CritterStackFixture.ArrangedEventFields(typeof(Proposed), null).ShouldBeEmpty();
    }

    [Fact]
    public void a_horizontal_table_is_one_row_of_the_events_own_fields()
    {
        var fields = CritterStackFixture.ArrangedEventFields(typeof(Proposed),
            table(["OwnerId", "ProposedFor"], ["0e5e0001-0000-0000-0000-000000000001", "2026-10-01T15:00:00Z"]));

        fields["OwnerId"].ShouldBe("0e5e0001-0000-0000-0000-000000000001");
        fields["ProposedFor"].ShouldBe("2026-10-01T15:00:00Z");
    }

    [Fact]
    public void a_second_horizontal_row_is_refused_because_the_step_describes_one_event()
    {
        var ex = Should.Throw<SpecCriticalException>(() =>
            CritterStackFixture.ArrangedEventFields(typeof(Proposed),
                table(["OwnerId"], ["a"], ["b"])));

        ex.Message.ShouldContain("one table row");
    }

    [Fact]
    public void a_field_value_header_turns_the_table_on_its_side()
    {
        var fields = CritterStackFixture.ArrangedEventFields(typeof(Proposed),
            table(["Field", "Value"],
                ["OwnerId", "0e5e0001-0000-0000-0000-000000000001"],
                ["ProposedFor", "2026-10-01T15:00:00Z"]));

        // Case-insensitive on the header, and as many rows as the event has fields worth naming.
        fields.Count.ShouldBe(2);
        fields["OwnerId"].ShouldBe("0e5e0001-0000-0000-0000-000000000001");
        fields["ProposedFor"].ShouldBe("2026-10-01T15:00:00Z");
    }

    [Fact]
    public void a_field_named_twice_in_a_vertical_table_is_refused_by_name()
    {
        // Horizontally a repeated column is impossible to miss; vertically it is two rows apart, and
        // silently keeping the last would arrange a value the reader may not have meant.
        var ex = Should.Throw<SpecCriticalException>(() =>
            CritterStackFixture.ArrangedEventFields(typeof(Proposed),
                table(["field", "value"], ["OwnerId", "a"], ["OwnerId", "b"])));

        ex.Message.ShouldContain("OwnerId");
    }

    [Fact]
    public void two_columns_that_are_not_field_and_value_stay_horizontal()
    {
        var fields = CritterStackFixture.ArrangedEventFields(typeof(Proposed),
            table(["OwnerId", "ProposedFor"], ["x", "y"]));

        fields.Keys.ShouldBe(["OwnerId", "ProposedFor"]);
    }

    [Fact]
    public void the_header_is_the_whole_signal_so_a_field_and_value_event_reads_as_vertical()
    {
        // The documented cost of detecting orientation from the header: an event whose properties
        // are literally Field and Value cannot be arranged horizontally. Pinned so the trade-off
        // stays a decision rather than a surprise; such an event arranges vertically instead.
        var fields = CritterStackFixture.ArrangedEventFields(typeof(Proposed),
            table(["Field", "Value"], ["Field", "f"], ["Value", "v"]));

        fields["Field"].ShouldBe("f");
        fields["Value"].ShouldBe("v");
    }
}
