using Bobcat;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// The stack a reader actually wants: the frames that are Bobcat's own plumbing and the test runner's
/// taken out, and never silently.
/// </summary>
public class SpecStackTraceTests
{
    /// <summary>The real shape, from a projected async step — one useful frame out of five.</summary>
    private const string ProjectedAsyncStack =
        """
           at async Task Bobcat.Xunit.Samples.Grammars.AsyncGrammar.RenameFails(String name) in AsyncGrammar.cs:39
           at async Task Bobcat.MarkerStepRuntime.Track(Task task, IDisposable step) in MarkerStepRuntime.cs:25
        --- End of stack trace from previous location ---
           at async Task Specs.AsyncSpecs.an_asynchronous_step_that_throws() in SentenceVariationSpecs.cs:120
           at void Xunit.v3.TestRunner.MoveNext() in TestRunner.cs:170
           at async ValueTask Xunit.Sdk.ExceptionAggregator.RunAsync(Func<ValueTask> code) in ExceptionAggregator.cs:124
        """;

    [Fact]
    public void the_plumbing_frames_are_removed_and_counted()
    {
        var filtered = SpecStackTrace.Filter(ProjectedAsyncStack);

        // Two frames survive, and both earn it: the grammar method that threw, and the TEST that
        // called it. "Which test" is half the answer to "what broke" — only the step bracket, the
        // runner and the async machinery between them are noise.
        filtered.Frames.Select(x => x.Contains("AsyncGrammar.RenameFails")
                ? "grammar"
                : x.Contains("an_asynchronous_step_that_throws") ? "test" : x)
            .ShouldBe(["grammar", "test"]);

        // Three, not four: the "--- End of stack trace ---" separator is structural and is not counted
        // as a frame. Saying the number out loud is what keeps an edited stack trustworthy.
        filtered.Hidden.ShouldBe(3);
    }

    [Fact]
    public void a_generated_interceptor_frame_is_removed()
    {
        // Unavoidable — it is a consequence of C#'s interceptor feature, not a choice — so here is the
        // only place it can be dealt with.
        var filtered = SpecStackTrace.Filter(
            """
               at void Grammars.FactGrammar.ThisLineAlwaysThrowsExceptions() in FactGrammar.cs:68
               at void Bobcat.Generated.BobcatStepInterceptors.__BobcatStep20(FactGrammar receiver) in BobcatStepInterceptors.g.cs:423
            """);

        filtered.Frames.ShouldHaveSingleItem().ShouldContain("ThisLineAlwaysThrowsExceptions");
        filtered.Hidden.ShouldBe(1);
    }

    [Fact]
    public void a_stack_that_would_filter_to_nothing_is_returned_whole()
    {
        // A stack with no visible frames is worse than a noisy one: the reader learns nothing and
        // cannot even tell that a filter happened.
        var filtered = SpecStackTrace.Filter(
            """
               at void Xunit.v3.TestRunner.MoveNext() in TestRunner.cs:170
               at object System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(object obj)
            """);

        filtered.Frames.Count.ShouldBe(2);
        filtered.Hidden.ShouldBe(0);
    }

    [Fact]
    public void the_gherkin_lanes_own_plumbing_is_removed_too()
    {
        var filtered = SpecStackTrace.Filter(
            """
               at void Specs.WalletFixture.CreditWallet(Decimal amount) in WalletFixture.cs:22
               at async Task Bobcat.Engine.DelegateExecutionStep.Execute(IExecutionContext ctx)
               at async Task Bobcat.Engine.Executor.Execute(IExecutionContext context)
            """);

        filtered.Frames.ShouldHaveSingleItem().ShouldContain("WalletFixture.CreditWallet");
        filtered.Hidden.ShouldBe(2);
    }

    [Fact]
    public void an_empty_or_missing_stack_filters_to_nothing()
    {
        SpecStackTrace.Filter((string?)null).Frames.ShouldBeEmpty();
        SpecStackTrace.Filter("   ").Frames.ShouldBeEmpty();
        SpecStackTrace.Filter((Exception?)null).Hidden.ShouldBe(0);
    }

    [Fact]
    public void a_real_exception_filters_its_own_stack()
    {
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (Exception e)
        {
            var filtered = SpecStackTrace.Filter(e);

            // Whatever the runner's frames are on this platform, the frame that threw survives.
            filtered.Frames.ShouldContain(x => x.Contains(nameof(a_real_exception_filters_its_own_stack)));
        }
    }

    [Fact]
    public void a_frame_is_shortened_for_reading()
    {
        var shortened = SpecStackTrace.Shorten(
            "at Bobcat.Xunit.Samples.Grammars.AsyncGrammar.RenameFails(String name) in "
            + "/Users/jeremymiller/code/bobcat/src/Bobcat.Xunit.Samples/Grammars/AsyncGrammar.cs:line 39");

        shortened.ShouldBe("at AsyncGrammar.RenameFails(String name) in AsyncGrammar.cs:39");
    }

    [Fact]
    public void shortening_a_frame_with_no_file_information_keeps_it_readable()
        => SpecStackTrace.Shorten("at System.Number.ThrowOverflowException(TypeCode type)")
            .ShouldBe("at Number.ThrowOverflowException(TypeCode type)");

    [Fact]
    public void shortening_is_presentation_and_the_filter_is_not()
    {
        // Separate on purpose: whether to abbreviate is a viewer's decision about its own width, and
        // which frames are Bobcat's plumbing is not. The wire carries the filtered frames UNshortened.
        var filtered = SpecStackTrace.Filter(ProjectedAsyncStack);
        filtered.Frames.ShouldAllBe(x => x.Contains("Bobcat.Xunit.Samples") || x.Contains("Specs."));
    }
}
