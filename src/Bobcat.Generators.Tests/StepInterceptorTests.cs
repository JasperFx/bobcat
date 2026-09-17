using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #110: a call to a <c>[BobcatStep]</c> helper is intercepted, so the step reports itself
/// while the test that calls it is not edited.
/// </summary>
/// <remarks>
/// Written against the generated source rather than the parser, because the value of this feature
/// is entirely in what it emits: an interceptor that does not match its target is a compile error
/// in the consumer's build, and a step whose text lost its argument is silently useless.
/// </remarks>
public class StepInterceptorTests
{
    private const string Source =
        """
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public abstract class ContextBase
        {
            [BobcatStep("the events are published", Keyword = "Given")]
            internal Task PublishSingleThreaded() => Task.CompletedTask;

            [BobcatStep("the events are published on {threads} threads", Keyword = "Given")]
            internal Task PublishMultiThreaded(int threads) => Task.CompletedTask;

            internal Task NotAStep() => Task.CompletedTask;
        }

        [BobcatFeature("Rebuilding")]
        public class rebuilding_specs : ContextBase
        {
            public async Task a_test()
            {
                await PublishSingleThreaded();
                await PublishMultiThreaded(3);
                await NotAStep();
            }
        }
        """;

    private static string Generated() =>
        GeneratorHarness.Run(Source).GeneratedSource("BobcatStepInterceptors");

    [Fact]
    public void every_decorated_call_gets_an_interceptor()
    {
        var code = Generated();

        // Two decorated calls, and the undecorated one contributes nothing. Counting the
        // APPLICATIONS, not the string — the emitted file also declares the attribute itself.
        var applications = code.Split("[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(").Length - 1;
        applications.ShouldBe(2);
        code.ShouldNotContain("NotAStep");
    }

    [Fact]
    public void the_step_text_binds_arguments_from_the_call_site()
    {
        // The whole point of a template. Without this the step reads "on {threads} threads" and
        // says less than the code it replaced.
        Generated().ShouldContain("\"the events are published on 3 threads\"");
    }

    [Fact]
    public void the_receiver_is_the_declaring_type_and_the_interceptor_is_an_extension()
    {
        // Both are requirements of the interceptor feature rather than choices: naming the calling
        // class is a signature mismatch, and a non-extension static method is rejected outright.
        // Getting either wrong fails in the CONSUMER's build, which is why it is pinned here.
        var code = Generated();

        code.ShouldContain("this global::Specs.ContextBase receiver");
        code.ShouldContain("file static class BobcatStepInterceptors");
    }

    [Fact]
    public void an_async_step_ends_when_the_helper_does()
    {
        // Tracked rather than awaited, so the duration is the helper's work and the caller's
        // execution shape is unchanged.
        Generated().ShouldContain("MarkerStepRuntime.Track(receiver.PublishSingleThreaded()");
    }

    [Fact]
    public void the_emitted_namespace_is_the_constant_a_project_opts_into()
    {
        // Interceptors are enabled per namespace, so this string is a public contract: it is the
        // one line a consuming csproj adds, and it must not vary by assembly.
        Generated().ShouldContain("namespace Bobcat.Generated");
    }

    [Fact]
    public void nothing_is_emitted_when_no_step_is_called()
    {
        var outcome = GeneratorHarness.Run(
            """
            namespace Specs;
            public class ordinary { public void a_test() { } }
            """);

        Should.Throw<InvalidOperationException>(() => outcome.GeneratedSource("BobcatStepInterceptors"));
    }

    /// <summary>
    /// Issue #304 — a decorated call reports WHICH marker comment it ran under, decided here at
    /// compile time from the call site's line, because that is the only place the two facts (a
    /// comment that the compiler erases, a call site an interceptor is generated for) are both
    /// exact.
    /// </summary>
    private const string MarkedSource =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        // Declared here rather than referenced: the generator matches a test method on the
        // attribute's NAME so it needs no runner reference, and neither should this fixture.
        [AttributeUsage(AttributeTargets.Method)]
        public sealed class FactAttribute : Attribute;

        public abstract class ContextBase
        {
            [BobcatStep("the events are published", Keyword = "Given")]
            internal Task Publish() => Task.CompletedTask;

            [BobcatStep("the daemon starts", Keyword = "When")]
            internal Task StartDaemon() => Task.CompletedTask;

            [BobcatStep("the aggregates match", Keyword = "Then")]
            internal Task CheckAggregates() => Task.CompletedTask;
        }

        [BobcatFeature("Async daemon")]
        public class daemon_specs : ContextBase
        {
            [Fact]
            public async Task the_projection_catches_up()
            {
                await Publish();

                // Given the events are published
                await Publish();

                // When the projection daemon is running
                await StartDaemon();

                // Then every expected aggregate matches
                await CheckAggregates();
            }

            private async Task a_helper_that_is_not_a_test()
            {
                // Given something that is not this test's narrative
                await Publish();
            }
        }
        """;

    /// <summary>
    /// The third argument of each emitted <c>ScenarioRecorder.Step(...)</c> call, in source order.
    /// Read positionally rather than as "the last argument", because since issue #339 a call may
    /// carry the runtime arguments after the index.
    /// </summary>
    private static string[] declaredIndexArguments(string code)
        => code.Split('\n')
            .Where(line => line.Contains("ScenarioRecorder.Step("))
            .Select(line => line.Substring(line.IndexOf("Step(", StringComparison.Ordinal) + 5)
                .Split(',')[2].Trim().TrimEnd(';', ')').Trim())
            .ToArray();

    [Fact]
    public void a_call_reports_the_marker_comment_it_runs_under()
    {
        // Four calls in source order: one above every comment, then one under each of the three.
        // The first is -1 rather than 0 — a call before the narrative starts is under nothing, and
        // rounding it into the first step would put work under a sentence that had not been
        // written yet.
        var arguments = declaredIndexArguments(GeneratorHarness.Run(MarkedSource).GeneratedSource("BobcatStepInterceptors"));

        arguments.ShouldBe(["-1", "0", "1", "2", "-1"]);
    }

    [Fact]
    public void a_call_from_a_method_that_is_not_a_test_is_under_no_narrative()
    {
        // The last entry above. A comment in a helper method looks exactly like a marker comment,
        // but the steps in scope belong to whichever TEST is running, not to the helper — so the
        // honest answer is "nothing", and the runtime bounds-checks the index anyway.
        declaredIndexArguments(GeneratorHarness.Run(MarkedSource).GeneratedSource("BobcatStepInterceptors"))
            .Last().ShouldBe("-1");
    }

    // --- Issue #339: an argument that is not a literal binds from its runtime VALUE ---

    /// <summary>
    /// The store-vocabulary shape, which is where the literal-only substitution failed: a type,
    /// a minted id and a constructed command object, and not one literal among them.
    /// </summary>
    private const string StoreVocabulary =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public record ConfirmAppointment(Guid Id);
        public class Appointment;
        public class AppointmentConfirmed;

        public abstract class StoreContext
        {
            [BobcatStep("{aggregate} \"{id}\" has already recorded these events", Keyword = "Given")]
            internal Task GivenEvents(Type aggregate, Guid id) => Task.CompletedTask;

            [BobcatStep("{command} is posted to \"{route}\"", Keyword = "When")]
            internal Task WhenPosted(object command, string route) => Task.CompletedTask;

            [BobcatStep("{event} is emitted", Keyword = "Then")]
            internal Task ThenEmitted(Type @event) => Task.CompletedTask;

            [BobcatStep("the response is {status}", Keyword = "Then")]
            internal Task ThenResponseIs(int status) => Task.CompletedTask;
        }

        [BobcatFeature("Booking appointments")]
        public class booking_specs : StoreContext
        {
            public async Task a_test()
            {
                var id = Guid.NewGuid();

                await GivenEvents(typeof(Appointment), id);
                await WhenPosted(new ConfirmAppointment(id), "/api/scheduling/confirmappointment");
                await ThenEmitted(typeof(AppointmentConfirmed));
                await ThenResponseIs(400);
            }
        }
        """;

    [Fact]
    public void a_placeholder_no_literal_can_fill_is_handed_to_the_recorder_as_a_value()
    {
        var code = GeneratorHarness.Run(StoreVocabulary).GeneratedSource("BobcatStepInterceptors");

        // The template survives as written — substitution has moved to execution time, where the
        // value exists — and the ARGUMENTS now travel with it.
        // Raw string literals: the expected text contains the same \" escapes the generated
        // file does, and doubling them here would only hide what is being asserted.
        code.ShouldContain(
            """Step("Given", "{aggregate} \"{id}\" has already recorded these events", -1, new global::Bobcat.StepArgument[] { new("aggregate", aggregate), new("id", id) })""");

        code.ShouldContain(
            """Step("Then", "{event} is emitted", -1, new global::Bobcat.StepArgument[] { new("event", @event) })""");
    }

    [Fact]
    public void a_literal_still_binds_at_compile_time_and_carries_nothing()
    {
        var code = GeneratorHarness.Run(StoreVocabulary).GeneratedSource("BobcatStepInterceptors");

        // `the response is 400` was one of the 22 that already worked. It is the same string on
        // every run, so it stays a compile-time fact and the call allocates no array.
        code.ShouldContain("""Step("Then", "the response is 400", -1);""");
    }

    [Fact]
    public void a_step_mixing_a_literal_and_a_value_binds_each_where_it_can()
    {
        // The measured half-bound case: the route bound because it is a literal, {command} did
        // not because the argument is `new ConfirmAppointment(id)`.
        var code = GeneratorHarness.Run(StoreVocabulary).GeneratedSource("BobcatStepInterceptors");

        code.ShouldContain(
            """Step("When", "{command} is posted to \"/api/scheduling/confirmappointment\"", -1, new global::Bobcat.StepArgument[] { new("command", command) })""");
    }

    [Fact]
    public void a_placeholder_naming_no_parameter_is_reported_rather_than_left_on_the_canvas()
    {
        // Since #339 a surviving placeholder can only mean the name is wrong — the one case
        // nothing can ever fill. It used to look identical to the 73 that were simply deferred.
        var outcome = GeneratorHarness.Run(
            """
            using System.Threading.Tasks;
            using Bobcat;

            namespace Specs;

            public abstract class ContextBase
            {
                [BobcatStep("the events are published on {thread} threads", Keyword = "Given")]
                internal Task PublishMultiThreaded(int threads) => Task.CompletedTask;
            }

            [BobcatFeature("Rebuilding")]
            public class rebuilding_specs : ContextBase
            {
                public async Task a_test() => await PublishMultiThreaded(3);
            }
            """);

        var diagnostic = outcome.WithId("BOBCAT027").ShouldHaveSingleItem();
        diagnostic.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
        diagnostic.GetMessage().ShouldContain("{thread}");
        diagnostic.GetMessage().ShouldContain("it takes threads");
    }

    [Fact]
    public void a_template_whose_placeholders_all_bind_reports_nothing()
    {
        GeneratorHarness.Run(StoreVocabulary).WithId("BOBCAT027").ShouldBeEmpty();
        GeneratorHarness.Run(Source).WithId("BOBCAT027").ShouldBeEmpty();
    }

    [Fact]
    public void an_unmarked_test_class_still_emits_interceptors_with_no_attribution()
    {
        // The original Source has no [Fact] and no comments: every call reports -1, and the step
        // still records itself. Attribution is additive to a feature that works without it.
        declaredIndexArguments(Generated()).ShouldAllBe(x => x == "-1");
    }

    [Fact]
    public void a_void_helper_is_invoked_rather_than_returned()
    {
        // `return receiver.M();` is CS0127 on a void helper — in the CONSUMER's build, in a file
        // they cannot open. It survived to 0.19.0 because every check here read the generated TEXT
        // and no project in the repository compiled an interceptor until #304's end-to-end test.
        const string source =
            """
            using Bobcat;

            namespace Specs;

            public class steps
            {
                [BobcatStep("the cache is cleared")]
                internal void ClearCache() { }

                public void a_test() => ClearCache();
            }
            """;

        var code = GeneratorHarness.Run(source).GeneratedSource("BobcatStepInterceptors");

        code.ShouldContain("                receiver.ClearCache();");
        code.ShouldNotContain("return receiver.ClearCache();");
    }
}
