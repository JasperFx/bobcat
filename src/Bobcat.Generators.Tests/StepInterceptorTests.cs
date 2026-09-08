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
}
