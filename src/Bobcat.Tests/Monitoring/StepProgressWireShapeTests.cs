using System.Text.Json;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// The serialized shape of <see cref="StepProgress"/>, including its cells (issue #387).
/// </summary>
/// <remarks>
/// These events are read by a console with no assembly reference to warn it, so a renamed or
/// re-shaped member breaks an external consumer silently. Stoat adds the round-trip on its side once
/// this is released; until then this is the pin that stops the field drifting in the meantime.
/// </remarks>
public class StepProgressWireShapeTests
{
    private static readonly JsonSerializerOptions wire = new(JsonSerializerDefaults.Web);

    private static string json(MonitorEvent @event)
        => JsonSerializer.Serialize(@event, @event.GetType(), wire);

    [Fact]
    public void the_cells_ride_as_the_same_record_step_finished_uses()
    {
        var text = json(new StepProgress(Guid.NewGuid(), "F/S", "s1", null, null, null, 120,
            [new StepCell("total", "failed", "5", "4", "±0.01", 2)]));

        using var document = JsonDocument.Parse(text);
        var cell = document.RootElement.GetProperty("cells")[0];

        cell.GetProperty("name").GetString().ShouldBe("total");
        cell.GetProperty("status").GetString().ShouldBe("failed");
        cell.GetProperty("expected").GetString().ShouldBe("5");
        cell.GetProperty("actual").GetString().ShouldBe("4");
        cell.GetProperty("note").GetString().ShouldBe("±0.01");
        cell.GetProperty("rowIndex").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void an_update_with_nothing_to_say_about_cells_carries_null()
    {
        var text = json(new StepProgress(Guid.NewGuid(), "F/S", "s1", null, 1, 3, 40));

        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("cells").ValueKind.ShouldBe(JsonValueKind.Null);

        // The row tick it has always carried is untouched beside it.
        document.RootElement.GetProperty("row").GetInt32().ShouldBe(1);
        document.RootElement.GetProperty("totalRows").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void it_is_still_a_step_progress_on_the_wire()
    {
        MonitorEvent @event = new StepProgress(Guid.NewGuid(), "F/S", "s1", "waiting", null, null, 40);

        var text = JsonSerializer.Serialize(@event, wire);

        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("type").GetString().ShouldBe("step_progress");
    }

    [Fact]
    public void a_reader_that_predates_the_field_round_trips_an_event_that_has_it()
    {
        // Additive, not breaking: the field is optional with a null default, so deserializing a
        // payload without it is the old shape and deserializing one with it is the new.
        var old = JsonSerializer.Deserialize<StepProgress>(
            """
            {"runId":"11111111-1111-1111-1111-111111111111","uid":"F/S","stepId":"s1",
             "message":null,"row":1,"totalRows":3,"elapsedMs":40}
            """, wire);

        old!.Cells.ShouldBeNull();
        old.Row.ShouldBe(1);
    }
}
