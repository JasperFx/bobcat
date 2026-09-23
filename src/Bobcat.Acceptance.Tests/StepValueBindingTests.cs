using Bobcat;
using Shouldly;

namespace Bobcat.Acceptance.Tests;

/// <summary>
/// Issue #339, end to end in a real compilation: a store-shaped <c>[BobcatStep]</c> vocabulary —
/// types, minted ids and constructed command objects, with barely a literal in sight — renders
/// its steps as sentences instead of as templates.
/// </summary>
/// <remarks>
/// <para>
/// This is the shape that failed. The feature was designed for <c>PublishMultiThreaded(3)</c>,
/// where every argument is a literal and the call-site syntax is all the substitution needs. A
/// typed store vocabulary is the opposite by nature, and measured on CritterCrush's projected
/// lane 73 of 95 generated step texts carried a raw <c>{placeholder}</c> — the canvas showed
/// <c>{event} is emitted</c> sixteen times. The more faithfully the vocabulary was built, the
/// less of it rendered.
/// </para>
/// <para>
/// Interceptors actually intercept here (the project opts into <c>Bobcat.Generated</c> in its
/// csproj), so these assertions are about what a consumer's canvas would show — not about
/// generated text, which cannot tell you the substitution happened.
/// </para>
/// </remarks>
[BobcatFeature("Step value binding")]
public class StepValueBindingTests
{
    public sealed record ConfirmAppointment(Guid AppointmentId);

    public sealed class Appointment;

    public sealed class AppointmentConfirmed;

    // The generic form these three would prefer cannot be intercepted at all (CS0411 — the type
    // argument is not inferable from the call site), so the vocabulary takes a System.Type. That
    // workaround used to walk straight into the other constraint: typeof(X) is not a literal.
    [BobcatStep("{aggregate} \"{id}\" has already recorded these events", Keyword = "Given")]
    internal void GivenEvents(Type aggregate, Guid id)
    {
    }

    [BobcatStep("{command} is posted to \"{route}\"", Keyword = "When")]
    internal void WhenPosted(object command, string route)
    {
    }

    [BobcatStep("{event} is emitted", Keyword = "Then")]
    internal void ThenEmitted(Type @event)
    {
    }

    [BobcatStep("the response is {status}", Keyword = "Then")]
    internal void ThenResponseIs(int status)
    {
    }

    private static IReadOnlyList<string> stepsOf(Action body, string scenario)
    {
        using var recording = ScenarioRecorder.Begin("Step value binding", scenario, null, Guid.NewGuid());
        body();
        return recording.Steps.Select(x => x.ToString()).ToList();
    }

    [Fact]
    public void a_store_vocabulary_renders_as_sentences()
    {
        var id = Guid.NewGuid();

        var steps = stepsOf(() =>
        {
            GivenEvents(typeof(Appointment), id);
            WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
            ThenEmitted(typeof(AppointmentConfirmed));
            ThenResponseIs(404);
        }, "a store vocabulary renders as sentences");

        steps.ShouldBe(
        [
            $"Given Appointment \"{id}\" has already recorded these events",
            "When ConfirmAppointment is posted to \"/api/scheduling/confirmappointment\"",
            "Then AppointmentConfirmed is emitted",

            // `And`, not a second `Then`. Both helpers declare Keyword = "Then" and neither can
            // know it is the second of its block; the recorder does, and Gherkin has always been
            // written this way. This assertion is the end-to-end proof — a real vocabulary, called
            // from a real test, rendering through the real recorder.
            "And the response is 404"
        ]);

        // Not one placeholder left. That is the whole finding.
        steps.ShouldAllBe(x => !x.Contains('{'));
    }

    [Fact]
    public void a_keyword_named_parameter_compiles_and_binds()
    {
        // `{event}` is the first placeholder an Event Modeling vocabulary reaches for, and the
        // parameter it names is a C# keyword. The emitted interceptor used to write `Type event`
        // into the consumer's build, which does not compile — this test compiling is half the
        // assertion.
        stepsOf(() => ThenEmitted(typeof(AppointmentConfirmed)), "a keyword named parameter compiles and binds")
            .ShouldBe(["Then AppointmentConfirmed is emitted"]);
    }

    [Fact]
    public void a_value_that_renders_to_nothing_leaves_the_placeholder_standing()
    {
        // The floor is today's behaviour: a reader can see that something was not resolved.
        stepsOf(() => ThenEmitted(null!), "a value that renders to nothing leaves the placeholder standing")
            .ShouldBe(["Then {event} is emitted"]);
    }

    [Fact]
    public void a_decorated_helper_called_outside_a_scenario_still_reports_nothing()
    {
        // Rendering must not change the rule that makes the feature safe to adopt: a helper is
        // called from plenty of places that are not specifications.
        ScenarioRecorder.Current.ShouldBeNull();
        ThenEmitted(typeof(AppointmentConfirmed));
    }
}
