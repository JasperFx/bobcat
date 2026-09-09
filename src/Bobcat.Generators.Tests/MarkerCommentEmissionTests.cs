using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #110: the marker comments in a test body reach the runtime as declared steps.
/// </summary>
/// <remarks>
/// Written against the emitted source, because the parser being right is not the same as the
/// feature working — the steps only exist at runtime if something registers them, and the whole
/// promise of this authoring style is that the author writes nothing but comments.
/// </remarks>
public class MarkerCommentEmissionTests
{
    private const string Source =
        """
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        [BobcatFeature("Async daemon")]
        public class when_the_daemon_catches_up
        {
            [Fact]
            public async Task a_proposed_appointment_is_confirmed()
            {
                // Given a proposed appointment
                var id = System.Guid.NewGuid();

                // the daemon polls on its own schedule, which is why the wait below exists
                await Task.Yield();

                // When the owner confirms
                await Task.Yield();

                // Then it is confirmed
                await Task.Yield();
            }

            [Fact]
            public void a_test_with_no_markers()
            {
                // just a comment
            }
        }

        public class not_marked_at_all
        {
            [Fact]
            public void ignored()
            {
                // Given this class carries no [BobcatFeature]
            }
        }
        """;

    private static string Generated() =>
        GeneratorHarness.Run(Source).GeneratedSource("BobcatDeclaredSteps");

    [Fact]
    public void registers_the_declared_steps_under_the_scenario_identity()
    {
        var code = Generated();

        code.ShouldContain("""global::Bobcat.DeclaredSteps.Register("Async daemon/a proposed appointment is confirmed",""");
        code.ShouldContain("""new global::Bobcat.DeclaredStep("Given", "a proposed appointment",""");
        code.ShouldContain("""new global::Bobcat.DeclaredStep("When", "the owner confirms",""");
        code.ShouldContain("""new global::Bobcat.DeclaredStep("Then", "it is confirmed",""");
    }

    [Fact]
    public void an_ordinary_comment_is_not_registered()
    {
        // The one that decides whether this is usable on an existing suite: a real test is full of
        // explanatory comments, and promoting them to steps would make the rendering worse than none.
        Generated().ShouldNotContain("polls on its own schedule");
    }

    [Fact]
    public void a_test_with_no_markers_is_not_announced_as_a_scenario()
    {
        // Registering it empty would announce a scenario with nothing to say, which reads as a defect.
        Generated().ShouldNotContain("a_test_with_no_markers");
    }

    [Fact]
    public void an_unmarked_class_contributes_nothing()
    {
        Generated().ShouldNotContain("carries no");
    }

    [Fact]
    public void registration_runs_without_the_author_calling_anything()
    {
        // A module initializer is what lets opting in really be only comments — the steps have to
        // reach the runtime before the first test runs, and no test body can be made to carry them.
        Generated().ShouldContain("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    }

    [Fact]
    public void an_assembly_with_no_marked_class_gains_no_initializer()
    {
        // No initializer, no startup cost, for the overwhelming majority of assemblies that never
        // heard of this feature.
        var result = GeneratorHarness.Run("public class plain { public void nothing() {} }");

        var hintNames = result.Result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.HintName);

        hintNames.ShouldNotContain(x => x.Contains("BobcatDeclaredSteps"));
    }
}
