using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #212 phase 2/3 at the generator level: both sides of BOBCAT018 (one instance per module
/// type per fixture) and BOBCAT019 (a module the [IncludeGrammars] declaration cannot construct),
/// plus what the healthy declaration actually emits — eager construction with the attribute
/// literals as named arguments, and lazy in-scope construction when the constructor resolves
/// anything from the scenario.
/// </summary>
public class ModuleCompositionTests
{
    private const string featurePath = "Features/Composed.feature";

    private const string feature = """
        Feature: Composed

          Scenario: One
            Then the echo of "x" should be "pre-x"
        """;

    private const string module = """
        using Bobcat;

        namespace Specs;

        public class EchoModule
        {
            private readonly string _prefix;
            public EchoModule(string prefix = "") => _prefix = prefix;

            [Then("the echo of {string} should be {string}")]
            public string Echo(string value) => _prefix + value;
        }
        """;

    [Fact]
    public void a_healthy_parameterized_module_reports_no_composition_diagnostics_and_constructs_eagerly()
    {
        var outcome = GeneratorHarness.Run(module + """

            [IncludeGrammars(typeof(EchoModule), "pre-")]
            public class ComposedFixture : Fixture;
            """, (featurePath, feature));

        outcome.WithId("BOBCAT018").ShouldBeEmpty();
        outcome.WithId("BOBCAT019").ShouldBeEmpty();

        var generated = outcome.GeneratedSource("Composed_Feature");
        generated.ShouldContain("new global::Specs.EchoModule(prefix: \"pre-\")");
        // No scenario-scope parameter, so construction stays eager at plan-build time.
        generated.ShouldNotContain("??=");
    }

    [Fact]
    public void the_same_module_type_twice_on_one_fixture_is_BOBCAT018()
    {
        var outcome = GeneratorHarness.Run(module + """

            [IncludeGrammars(typeof(EchoModule), "a-")]
            [IncludeGrammars(typeof(EchoModule), "b-")]
            public class ComposedFixture : Fixture;
            """, (featurePath, feature));

        var diagnostic = outcome.WithId("BOBCAT018").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("EchoModule");
        diagnostic.GetMessage().ShouldContain("more than once");

        // The broken composition suppresses feature emission — the diagnostic is the story,
        // not a pile of C# errors in generated code.
        outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .ShouldNotContain(s => s.HintName.Contains("Composed_Feature"));
    }

    [Fact]
    public void the_same_module_type_twice_in_one_attribute_is_BOBCAT018()
    {
        var outcome = GeneratorHarness.Run(module + """

            [IncludeGrammars(typeof(EchoModule), typeof(EchoModule))]
            public class ComposedFixture : Fixture;
            """);

        outcome.WithId("BOBCAT018").ShouldHaveSingleItem();
    }

    [Fact]
    public void arguments_no_constructor_can_take_are_BOBCAT019()
    {
        var outcome = GeneratorHarness.Run(module + """

            [IncludeGrammars(typeof(EchoModule), "pre-", 42, true)]
            public class ComposedFixture : Fixture;
            """, (featurePath, feature));

        var diagnostic = outcome.WithId("BOBCAT019").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("EchoModule");
        diagnostic.GetMessage().ShouldContain("ComposedFixture");
    }

    [Fact]
    public void a_required_value_parameter_no_argument_covers_is_BOBCAT019()
    {
        var outcome = GeneratorHarness.Run("""
            using Bobcat;

            namespace Specs;

            public class RouteModule
            {
                public RouteModule(string route) { }

                [When("something happens")]
                public void Act() { }
            }

            [IncludeGrammars(typeof(RouteModule))]
            public class ComposedFixture : Fixture;
            """);

        outcome.WithId("BOBCAT019").ShouldHaveSingleItem()
            .GetMessage().ShouldContain("route");
    }

    [Fact]
    public void a_scenario_scope_constructor_parameter_switches_to_lazy_in_scope_construction()
    {
        var outcome = GeneratorHarness.Run("""
            using Bobcat;
            using Bobcat.Engine;

            namespace Specs;

            public class BoundModule
            {
                public BoundModule(string label, IStepContext context) { }

                [When("the bound module acts")]
                public void Act() { }
            }

            [IncludeGrammars(typeof(BoundModule), "wallet")]
            public class ComposedFixture : Fixture;
            """, (featurePath, """
            Feature: Composed

              Scenario: One
                When the bound module acts
            """));

        outcome.WithId("BOBCAT019").ShouldBeEmpty();

        var generated = outcome.GeneratedSource("Composed_Feature");
        generated.ShouldContain("global::Specs.BoundModule? __m0 = null;");
        generated.ShouldContain("__m0 ??= new global::Specs.BoundModule(label: \"wallet\", context: ctx)");
    }

    [Fact]
    public void include_grammars_on_a_base_class_is_discovered_and_the_most_derived_wins()
    {
        var outcome = GeneratorHarness.Run(module + """

            [IncludeGrammars(typeof(EchoModule), "base-")]
            public abstract class BaseFixture : Fixture;

            [IncludeGrammars(typeof(EchoModule), "derived-")]
            public class ComposedFixture : BaseFixture;
            """, (featurePath, feature));

        // Re-declaring a base module on the derived class is the override, not a duplicate.
        outcome.WithId("BOBCAT018").ShouldBeEmpty();

        var generated = outcome.GeneratedSource("Composed_Feature");
        generated.ShouldContain("new global::Specs.EchoModule(prefix: \"derived-\")");
        generated.ShouldNotContain("\"base-\"");
    }

    [Fact]
    public void the_legacy_multi_module_form_still_composes_with_no_arguments()
    {
        var outcome = GeneratorHarness.Run("""
            using Bobcat;

            namespace Specs;

            public class FirstModule
            {
                [When("first acts")] public void Act() { }
            }

            public class SecondModule
            {
                [When("second acts")] public void Act() { }
            }

            [IncludeGrammars(typeof(FirstModule), typeof(SecondModule))]
            public class ComposedFixture : Fixture;
            """, (featurePath, """
            Feature: Composed

              Scenario: One
                When first acts
                When second acts
            """));

        outcome.WithId("BOBCAT018").ShouldBeEmpty();
        outcome.WithId("BOBCAT019").ShouldBeEmpty();

        var generated = outcome.GeneratedSource("Composed_Feature");
        generated.ShouldContain("new global::Specs.FirstModule()");
        generated.ShouldContain("new global::Specs.SecondModule()");
    }
}
