using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#484: a project with Bobcat step calls to intercept but no <c>InterceptorsNamespaces</c> opt-in
/// for <c>Bobcat.Generated</c> is told so as BOBCAT037 -- once, at the first call site -- instead of
/// getting a CS9137 per call site in a generated file, or, with that silenced, helpers that run as
/// plain method calls and a SpecAssert wrong that lands nowhere.
/// </summary>
public class InterceptorOptInDiagnosticTests
{
    private const string Spec =
        """
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public class AccountGrammar
        {
            [Given("an account with {balance}")]
            internal void AnAccount(int balance) { }

            [Then("the balance is {balance}")]
            internal void TheBalanceIs(int balance) => SpecAssert.Check("balance", 0, balance);
        }

        [BobcatFeature("Accounts")]
        public class AccountSpecs
        {
            private readonly AccountGrammar _grammar = new();

            public void a_deposit_is_credited()
            {
                _grammar.AnAccount(100);
                _grammar.TheBalanceIs(150);
            }
        }
        """;

    [Fact]
    public void fires_once_at_the_first_call_site_when_interceptors_are_off()
    {
        var outcome = GeneratorHarness.RunWithoutInterceptors(Spec);

        var error = outcome.WithId("BOBCAT037").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("2 Bobcat step calls to intercept");
        error.GetMessage().ShouldContain(
            "<InterceptorsNamespaces>$(InterceptorsNamespaces);Bobcat.Generated</InterceptorsNamespaces>");
        error.GetMessage().ShouldContain("buildTransitive/Bobcat.Generators.props");

        // At the first call in the file, so the error is somewhere the author can click
        var text = error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan);
        text.ShouldContain("AnAccount(100)");
    }

    [Fact]
    public void emits_no_interceptors_beside_it_so_the_build_says_one_thing()
    {
        var outcome = GeneratorHarness.RunWithoutInterceptors(Spec);

        outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.HintName)
            .ShouldNotContain(x => x.Contains("BobcatStepInterceptors") || x.Contains("BobcatPlannedSteps"));

        // Without the guard every interceptor was its own CS9137
        outcome.CompilationErrors.ShouldNotContain(x => x.Id == "CS9137");
    }

    [Fact]
    public void the_preview_spelling_counts_as_opted_in()
    {
        var parse = CSharpParseOptions.Default
            .WithFeatures([new KeyValuePair<string, string>("InterceptorsPreviewNamespaces", "Mine;Bobcat.Generated")]);

        DeclaredValues.InterceptorsEnabled(parse).ShouldBeTrue();
    }

    [Fact]
    public void silent_when_interceptors_are_on()
    {
        var outcome = GeneratorHarness.Run(Spec);

        outcome.WithId("BOBCAT037").ShouldBeEmpty();
        outcome.GeneratedSource("BobcatStepInterceptors").ShouldContain("InterceptsLocation");
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void silent_for_a_compilation_with_nothing_to_intercept()
    {
        // Specs written entirely in marker comments, and an ordinary test: no step helper is called,
        // so there is nothing the missing opt-in could break
        var outcome = GeneratorHarness.RunWithoutInterceptors(
            """
            using Bobcat;

            namespace Specs;

            [BobcatFeature("Accounts")]
            public class AccountSpecs
            {
                public void a_deposit_is_credited()
                {
                    // Given an account with 100
                    // When 50 is deposited
                    // Then the balance is 150
                }

                public void not_a_spec() => System.Console.WriteLine("hello");
            }
            """);

        outcome.WithId("BOBCAT037").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }
}
