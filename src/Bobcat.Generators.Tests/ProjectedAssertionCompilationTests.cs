using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Every Shouldly assertion the generator claims is generated AND COMPILED here (issue #410).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <c>AssertionComparisonTests</c> pins the dialect's mapping — a pure
/// function from a method NAME to a <c>Comparison</c>, testable without a compilation, which is the
/// deliberate design. Nothing tested that a claimed name could actually be <i>intercepted</i>, and
/// the two halves live in different assemblies because the netstandard2.0 generator cannot
/// reference the runtime. So the only thing that failed was a downstream consumer's build:
/// <c>ShouldNotBeNull&lt;T&gt;</c> and <c>ShouldBeOfType&lt;T&gt;</c> return <c>T</c> while the
/// interceptor was always emitted as <c>void</c>, which is CS9144 in a generated file the author
/// cannot edit, over an assertion that compiles perfectly without Bobcat.
/// </para>
/// <para>
/// A name added to the dialect without a row here is a name nothing has ever compiled.
/// </para>
/// </remarks>
public class ProjectedAssertionCompilationTests
{
    /// <summary>
    /// Projected assertions are opt-in per project, so a test that omits this generates no
    /// interceptors and every assertion about them passes over nothing.
    /// </summary>
    private static readonly Dictionary<string, string> _projectedAssertionsOn =
        new() { ["build_property.BobcatProjectAssertions"] = "true" };

    private static string specCalling(string statement) =>
        $$"""
        using System.Collections.Generic;
        using Bobcat;
        using Shouldly;

        namespace Specs;

        // Declared locally rather than referencing xunit in the harness: the generator matches a
        // test attribute by SHORT NAME (MarkerCommentSpecs.IsTestMethod), so this is the same
        // trigger a real [Fact] is, without a second test framework in the reference set.
        public sealed class FactAttribute : System.Attribute;

        [BobcatFeature("Assertions")]
        public class assertion_specs
        {
            [Fact]
            public void a_test()
            {
                var count = 7;
                var ratio = 1.0;
                var name = "abc";
                string? maybe = "abc";
                object boxed = "abc";
                var items = new List<int>();

                {{statement}}
            }
        }
        """;

    [Theory]
    [InlineData("count.ShouldBe(7);")]
    [InlineData("count.ShouldNotBe(8);")]
    [InlineData("count.ShouldBeGreaterThan(1);")]
    [InlineData("count.ShouldBeGreaterThanOrEqualTo(1);")]
    [InlineData("count.ShouldBeLessThan(10);")]
    [InlineData("count.ShouldBeLessThanOrEqualTo(10);")]
    [InlineData("name.ShouldContain(\"a\");")]
    [InlineData("name.ShouldStartWith(\"a\");")]
    [InlineData("name.ShouldEndWith(\"c\");")]
    [InlineData("maybe.ShouldBeNull();")]
    [InlineData("items.ShouldBeEmpty();")]
    [InlineData("ratio.ShouldBe(1.0, 0.01);")]

    // The two that were broken, and the reason this class exists. Both return T.
    [InlineData("maybe.ShouldNotBeNull();")]
    [InlineData("boxed.ShouldBeOfType<string>();")]
    public void the_generated_interceptor_compiles(string statement)
    {
        var outcome = GeneratorHarness.Run(specCalling(statement), _projectedAssertionsOn);

        // FIRST, that something was generated at all. Without this the theory passes for a spec
        // shape the generator ignores — which is exactly how it was first written, and 15 rows went
        // green over an empty generation. A compile check on nothing compiles.
        var source = outcome.GeneratedSource("Interceptors");
        source.ShouldContain("InterceptsLocation");

        // CS9144 would show up here and nowhere else in this repository.
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void a_value_returning_assertion_hands_its_result_back()
    {
        var source = GeneratorHarness
            .Run(specCalling("maybe.ShouldNotBeNull();"), _projectedAssertionsOn)
            .GeneratedSource("Interceptors");

        // Captured inside the Gather lambda and returned, so the signature matches and the call site
        // behaves as it did. Safe to return default after a GATHERED failure only because the
        // generator intercepts statement-level calls alone — the value is discarded by definition,
        // and a chained `x.ShouldNotBeNull().Name` is never intercepted at all.
        source.ShouldContain("__result = default!");
        source.ShouldContain("() => __result = global::Shouldly");
        source.ShouldContain("return __result;");
    }

    [Fact]
    public void a_void_assertion_is_unchanged_by_that()
    {
        var source = GeneratorHarness
            .Run(specCalling("count.ShouldBe(7);"), _projectedAssertionsOn)
            .GeneratedSource("Interceptors");

        source.ShouldContain("internal static void __BobcatAssert");
        source.ShouldNotContain("__result");
    }

    [Fact]
    public void an_enum_default_is_reproduced_as_a_cast_not_as_its_number()
    {
        var source = GeneratorHarness
            .Run(specCalling("name.ShouldContain(\"a\");"), _projectedAssertionsOn)
            .GeneratedSource("Interceptors");

        // `Case caseSensitivity = ...`. The number alone is CS1750 in the consumer's build, and
        // `default` would be right only for a zero-valued member — so the cast is what makes it
        // right whatever the default is, and the assertion is about the cast rather than the value.
        source.ShouldMatch(@"\(global::Shouldly\.Case\)\(\d+\)");
    }

    [Fact]
    public void a_signature_the_generator_cannot_reproduce_is_left_alone()
    {
        // The guard, stated against the rule rather than against a Shouldly overload that happens
        // to exist: by-ref parameters and by-ref returns cannot survive the Gather lambda, and an
        // interceptor that cannot match must not be emitted at all. #384's degradation — no cell,
        // a plain step line — extended from "no comparison for this name" to "no signature that can
        // match this call".
        var byValue = typeof(Shouldly.ShouldBeTestExtensions)
            .GetMethods()
            .First(m => m.Name == nameof(Shouldly.ShouldBeTestExtensions.ShouldBe));

        byValue.GetParameters().ShouldAllBe(p => !p.ParameterType.IsByRef);
    }
}
