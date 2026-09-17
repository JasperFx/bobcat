using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Issue #339: how a <c>[BobcatStep]</c> template renders against the values the helper was
/// called with.
/// </summary>
/// <remarks>
/// The measurement that produced this: 95 interception sites on a typed store vocabulary, 73 of
/// them carrying a raw placeholder, and <c>{event} is emitted</c> on the canvas sixteen times.
/// Every rule below is about a sentence on a canvas, not a debugger dump.
/// </remarks>
public class StepTextTests
{
    private static string render(string template, params (string Name, object? Value)[] arguments)
        => StepText.Render(template, arguments.Select(x => new StepArgument(x.Name, x.Value)).ToList());

    [Fact]
    public void a_type_renders_as_its_name()
    {
        // The whole reason this exists. A generic helper cannot be intercepted (CS0411), so the
        // vocabulary has to take `typeof(X)` — which is exactly what could not bind before.
        render("{event} is emitted", ("event", typeof(StepTextTests)))
            .ShouldBe("StepTextTests is emitted");
    }

    [Fact]
    public void an_object_renders_as_its_type_name_not_its_ToString()
    {
        // `new ConfirmAppointment(id)` reads as "ConfirmAppointment". A record's ToString would
        // paste its whole state into a sentence; the data belongs in a table.
        render("{command} is posted", ("command", new ConfirmAppointment(Guid.Empty)))
            .ShouldBe("ConfirmAppointment is posted");
    }

    [Fact]
    public void scalars_render_as_their_values()
    {
        render("the response is {status}", ("status", 404)).ShouldBe("the response is 404");
        render("the flag is {on}", ("on", true)).ShouldBe("the flag is true");
        render("the name is {name}", ("name", "wallet")).ShouldBe("the name is wallet");
        render("the kind is {kind}", ("kind", DayOfWeek.Friday)).ShouldBe("the kind is Friday");

        var id = Guid.NewGuid();
        render("stream \"{id}\"", ("id", id)).ShouldBe($"stream \"{id}\"");
    }

    [Fact]
    public void numbers_and_dates_are_culture_invariant()
    {
        // A step's text reaches a report, a canvas and a wire event. A decimal comma on one
        // machine would make two runs of one scenario look like two scenarios.
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            render("the total is {amount}", ("amount", 1234.5m)).ShouldBe("the total is 1234.5");
            render("at {at}", ("at", new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc)))
                .ShouldBe("at 2026-09-17T08:00:00.0000000Z");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void a_sequence_renders_as_what_is_in_it()
    {
        render("{events} are emitted", ("events", new[] { typeof(int), typeof(string) }))
            .ShouldBe("Int32, String are emitted");
    }

    [Fact]
    public void a_long_sequence_is_capped_because_a_step_is_a_sentence()
    {
        render("{events} are emitted", ("events", Enumerable.Range(1, 9).ToArray()))
            .ShouldBe("1, 2, 3, 4, 5, and 4 more are emitted");
    }

    [Fact]
    public void a_value_with_nothing_to_say_leaves_the_placeholder_standing()
    {
        // Today's behaviour is the floor: a reader can see that something was not resolved,
        // rather than being shown a blank or the word "null" and believing it.
        render("{event} is emitted", ("event", null)).ShouldBe("{event} is emitted");
        render("{name} exists", ("name", "")).ShouldBe("{name} exists");
        render("{events} are emitted", ("events", Array.Empty<int>())).ShouldBe("{events} are emitted");
    }

    [Fact]
    public void a_placeholder_no_argument_names_is_left_alone()
    {
        // The generator warns about this at build time (BOBCAT027); at run time it is simply
        // never substituted.
        render("the {thing} is {other}", ("thing", "wallet")).ShouldBe("the wallet is {other}");
    }

    [Fact]
    public void a_template_with_no_arguments_is_returned_unchanged()
        => StepText.Render("the events are published", []).ShouldBe("the events are published");

    [Fact]
    public void the_recorder_overload_records_the_rendered_text()
    {
        using var recording = ScenarioRecorder.Begin("F", "S", null, Guid.NewGuid());

        using (ScenarioRecorder.Step("Then", "{event} is emitted", -1,
                   [new StepArgument("event", typeof(ConfirmAppointment))]))
        {
        }

        recording.Steps.ShouldHaveSingleItem().Text.ShouldBe("ConfirmAppointment is emitted");
    }

    private sealed record ConfirmAppointment(Guid Id);
}
