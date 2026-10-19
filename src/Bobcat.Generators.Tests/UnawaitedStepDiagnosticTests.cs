using Microsoft.CodeAnalysis;
using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#623: a <c>Task</c>-returning step called as a statement and never awaited is BOBCAT043 at the call, because
/// its assertion runs unobserved. CS4014 covers only an <c>async</c> method, and nothing covers a synchronous one.
/// </summary>
public class UnawaitedStepDiagnosticTests
{
    private static string specWith(string body, string testModifiers = "async Task") =>
        $$"""
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Bobcat.CritterStack;

        namespace Specs;

        public class Account { public Guid Id { get; set; } }

        public class Steps
        {
            [Then("the balance is {amount}")]
            public Task TheBalanceIs(int amount) => Task.CompletedTask;

            [Then("the statement is {amount}")]
            public ValueTask<bool> TheStatementIs(int amount) => new(true);

            [Then("the account is open")]
            public void TheAccountIsOpen() { }

            public Task NotAStep() => Task.CompletedTask;
        }

        [BobcatFeature("Accounts")]
        public class AccountSpecs : CritterStackSpec
        {
            public AccountSpecs() : base((IServiceProvider)null!) { }

            private readonly Steps steps = new();

            public {{testModifiers}} a_deposit_is_credited()
            {
                {{body}}
            }
        }
        """;

    private static IReadOnlyList<Diagnostic> reported(string body, string testModifiers = "async Task")
        => GeneratorHarness.Run(specWith(body, testModifiers), null, [CritterStackStandIn.Reference]).WithId("BOBCAT043").ToList();

    [Fact]
    public void a_step_helper_returning_a_task_called_as_a_statement_is_reported_at_the_call()
    {
        var warning = reported("steps.TheBalanceIs(100);").ShouldHaveSingleItem();

        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.GetMessage().ShouldBe(
            "'TheBalanceIs' returns Task and is not awaited, so its step runs unobserved: it may finish after the " +
            "scenario has closed, and a failure in it never fails the test. Add 'await' (and make the test async).");
        warning.Location.SourceTree!.GetText().ToString(warning.Location.SourceSpan).ShouldBe("TheBalanceIs");
    }

    [Fact]
    public void a_role_attributed_vocabulary_method_read_from_metadata_is_reported()
    {
        var warning = reported("ThenDocument<Account>(Guid.NewGuid());").ShouldHaveSingleItem();

        warning.GetMessage().ShouldStartWith("'ThenDocument' returns Task<Account> and is not awaited");
    }

    [Fact]
    public void a_synchronous_test_where_the_compiler_says_nothing_is_reported_too()
        => reported("steps.TheBalanceIs(100);", testModifiers: "void").ShouldHaveSingleItem();

    [Fact]
    public void a_value_task_step_and_a_discard_are_reported()
        => reported("_ = steps.TheStatementIs(100);").ShouldHaveSingleItem()
            .GetMessage().ShouldStartWith("'TheStatementIs' returns ValueTask<bool>");

    [Theory]
    [InlineData("await steps.TheBalanceIs(100);")]
    [InlineData("var pending = steps.TheBalanceIs(100); await pending;")]
    [InlineData("await Task.WhenAll(steps.TheBalanceIs(1), steps.TheBalanceIs(2));")]
    [InlineData("steps.TheAccountIsOpen();")]
    [InlineData("steps.NotAStep();")]
    [InlineData("ThenEvents(typeof(Account));")]
    public void an_awaited_or_handed_on_task_a_synchronous_step_and_a_method_that_is_no_step_are_not(string body)
        => reported(body).ShouldBeEmpty();
}
