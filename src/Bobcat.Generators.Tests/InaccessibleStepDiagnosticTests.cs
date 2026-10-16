using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#535: a step helper the generated interceptor cannot call is BOBCAT039 at the helper, instead of a
/// CS0122 per call site in <c>BobcatStepInterceptors.g.cs</c>.
/// </summary>
public class InaccessibleStepDiagnosticTests
{
    private static string specWith(string accessibility) =>
        $$"""
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public abstract class AccountSpec
        {
            [Given("an account with {balance}")]
            {{accessibility}} Task AnAccount(int balance) => Task.CompletedTask;
        }

        [BobcatFeature("Accounts")]
        public class AccountSpecs : AccountSpec
        {
            public async Task a_deposit_is_credited()
            {
                await AnAccount(100);
                await AnAccount(200);
            }
        }
        """;

    [Theory]
    [InlineData("protected", "protected")]
    [InlineData("private protected", "private protected")]
    public void a_step_only_a_derived_class_can_call_is_reported_once_at_its_declaration(string accessibility, string described)
    {
        var outcome = GeneratorHarness.Run(specWith(accessibility));

        var error = outcome.WithId("BOBCAT039").ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.GetMessage().ShouldBe(
            $"Step helper 'AnAccount' is {described}, so Bobcat's generated interceptor cannot call it and its " +
            "calls would record no step. Make it public or internal.");

        var text = error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan);
        text.ShouldBe("AnAccount");
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("private protected")]
    public void no_interceptor_is_emitted_for_it_so_the_build_says_one_thing(string accessibility)
    {
        var outcome = GeneratorHarness.Run(specWith(accessibility));

        outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .Select(s => s.HintName)
            .ShouldNotContain(x => x.Contains("BobcatStepInterceptors"));
        outcome.CompilationErrors.ShouldNotContain(x => x.Id == "CS0122");
    }

    [Fact]
    public void a_private_step_called_from_its_own_class_is_reported()
    {
        var outcome = GeneratorHarness.Run(
            """
            using Bobcat;

            namespace Specs;

            [BobcatFeature("Accounts")]
            public class AccountSpecs
            {
                [Given("an account")]
                private void AnAccount() { }

                public void a_deposit_is_credited() => AnAccount();
            }
            """);

        outcome.WithId("BOBCAT039").ShouldHaveSingleItem().GetMessage().ShouldContain("'AnAccount' is private");
        outcome.CompilationErrors.ShouldNotContain(x => x.Id == "CS0122");
    }

    [Fact]
    public void a_public_step_on_a_private_nested_type_is_reported()
    {
        var outcome = GeneratorHarness.Run(
            """
            using Bobcat;

            namespace Specs;

            [BobcatFeature("Accounts")]
            public class AccountSpecs
            {
                private class Grammar
                {
                    [Given("an account")]
                    public void AnAccount() { }
                }

                public void a_deposit_is_credited() => new Grammar().AnAccount();
            }
            """);

        outcome.WithId("BOBCAT039").ShouldHaveSingleItem().GetMessage()
            .ShouldContain("declared on 'Specs.AccountSpecs.Grammar', which the rest of the assembly cannot reach");
        outcome.CompilationErrors.ShouldNotContain(x => x.Id == "CS0122");
    }

    [Theory]
    [InlineData("public")]
    [InlineData("internal")]
    [InlineData("protected internal")]
    public void silent_for_a_step_the_assembly_can_reach(string accessibility)
    {
        var outcome = GeneratorHarness.Run(specWith(accessibility));

        outcome.WithId("BOBCAT039").ShouldBeEmpty();
        outcome.GeneratedSource("BobcatStepInterceptors").ShouldContain("receiver.AnAccount(balance)");
        outcome.CompilationErrors.ShouldBeEmpty();
    }

    [Fact]
    public void the_reachable_steps_of_a_test_are_still_intercepted()
    {
        var outcome = GeneratorHarness.Run(
            """
            using Bobcat;

            namespace Specs;

            public abstract class AccountSpec
            {
                [Given("an account")]
                protected void AnAccount() { }

                [Then("the balance is {balance}")]
                public void TheBalanceIs(int balance) { }
            }

            [BobcatFeature("Accounts")]
            public class AccountSpecs : AccountSpec
            {
                public void a_deposit_is_credited()
                {
                    AnAccount();
                    TheBalanceIs(100);
                }
            }
            """);

        outcome.WithId("BOBCAT039").ShouldHaveSingleItem();
        var code = outcome.GeneratedSource("BobcatStepInterceptors");
        code.ShouldContain("receiver.TheBalanceIs(balance)");
        code.ShouldNotContain("AnAccount");
        outcome.CompilationErrors.ShouldBeEmpty();
    }
}
