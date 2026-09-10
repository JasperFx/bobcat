using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #259: a scenario tagged <c>@arrangement</c> is a named list of Given steps that never runs
/// as a test. The generator inlines it wherever a Given step's text is its name, so a history most
/// scenarios in a chapter share is written once — and every inlined step still binds, resolves its
/// captures and stamps roles exactly as it would have written out longhand.
/// </summary>
public class NamedArrangementTests
{
    private const string Fixture =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public record WalletOpened(Guid WalletId, string Owner);
        public record WalletCredited(Guid WalletId, decimal Amount);
        public record CreditWallet(Guid WalletId, decimal Amount);

        [FixtureTitle("Wallet")]
        public class WalletFixture : Fixture
        {
            [Given("no events for {aggregate} {string}")]
            public void GivenNoEvents(Type aggregate, string id) { }

            [Given("{event} occurred")]
            public Task GivenEventOccurred(Type @event, StepTable? fields) => Task.CompletedTask;

            [When("{command} is received")]
            public Task WhenReceived(Type command, StepTable? fields) => Task.CompletedTask;

            [Given("a wallet exists")]
            public void AWalletExists() { }
        }

        public class Wallet { }
        """;

    private const string OpenWallet =
        """
          @arrangement
          Scenario: an open wallet
            Given WalletOpened occurred
              | Owner |
              | Hal   |
        """;

    private static string feature(params string[] blocks)
        => "Feature: Wallet\n\n" + string.Join("\n\n", blocks) + "\n";

    private static string scenarioUsing(string reference, string keyword = "And")
        => $"""
              Scenario: Crediting
                Given no events for Wallet "11111111-1111-1111-1111-111111111111"
                {keyword} {reference}
                When CreditWallet is received
            """;

    private static GeneratorHarness.RunOutcome run(string featureText)
        => GeneratorHarness.Run(Fixture, ("Wallet.feature", featureText));

    private static bool emittedTheFeature(GeneratorHarness.RunOutcome outcome)
        => outcome.Result.Results.SelectMany(r => r.GeneratedSources)
            .Any(s => s.HintName.Contains("Wallet_Feature", StringComparison.Ordinal));

    [Fact]
    public void an_arrangement_is_inlined_where_its_name_is_used_and_never_runs_itself()
    {
        var outcome = run(feature(OpenWallet, scenarioUsing("an open wallet")));

        outcome.WithId("BOBCAT002").ShouldBeEmpty();
        outcome.WithId("BOBCAT021").ShouldBeEmpty();
        outcome.WithId("BOBCAT022").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();

        var source = outcome.GeneratedSource("Wallet");
        source.ShouldContain("WalletOpened occurred");
        source.ShouldContain("typeof(global::Specs.WalletOpened)");

        // Not a scenario, not a step: the name is gone once its steps are in place.
        source.ShouldNotContain("an open wallet");
    }

    [Fact]
    public void an_arrangement_may_be_declared_after_the_scenario_using_it()
    {
        var outcome = run(feature(scenarioUsing("an open wallet"), OpenWallet));

        outcome.WithId("BOBCAT002").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
        outcome.GeneratedSource("Wallet").ShouldContain("WalletOpened occurred");
    }

    [Fact]
    public void an_arrangement_builds_on_another_and_inlines_in_order()
    {
        const string credited =
            """
              @arrangement
              Scenario: a credited wallet
                Given an open wallet
                And WalletCredited occurred
                  | Amount |
                  | 40     |
            """;

        var outcome = run(feature(OpenWallet, credited, scenarioUsing("a credited wallet")));

        outcome.WithId("BOBCAT022").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();

        var source = outcome.GeneratedSource("Wallet");
        source.IndexOf("WalletOpened occurred", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("WalletCredited occurred", StringComparison.Ordinal));
    }

    [Fact]
    public void a_misspelled_reference_is_a_build_error_that_names_the_arrangement_it_missed()
    {
        var outcome = run(feature(OpenWallet, scenarioUsing("an opn wallet")));

        var diagnostic = outcome.WithId("BOBCAT021").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("'an open wallet'");

        // Reported once, as the specific error — not also as a generic unmatched step.
        outcome.WithId("BOBCAT002").ShouldBeEmpty();
    }

    [Fact]
    public void an_unmatched_step_nowhere_near_an_arrangement_is_still_an_unmatched_step()
    {
        var outcome = run(feature(OpenWallet, scenarioUsing("the moon is full")));

        outcome.WithId("BOBCAT002").ShouldHaveSingleItem();
        outcome.WithId("BOBCAT021").ShouldBeEmpty();
    }

    [Fact]
    public void an_arrangement_holds_given_steps_only()
    {
        // A When inside an arrangement would hand the scenario an act it never wrote — and the
        // Event Model reads the last When as the slice's command.
        const string acting =
            """
              @arrangement
              Scenario: an open wallet
                Given WalletOpened occurred
                When CreditWallet is received
            """;

        var outcome = run(feature(acting, scenarioUsing("an open wallet")));

        outcome.WithId("BOBCAT022").ShouldHaveSingleItem().GetMessage().ShouldContain("Given steps only");
        emittedTheFeature(outcome).ShouldBeFalse();
    }

    [Fact]
    public void an_arrangement_is_referenced_from_a_given()
    {
        var outcome = run(feature(OpenWallet, scenarioUsing("an open wallet", keyword: "When")));

        outcome.WithId("BOBCAT022").ShouldHaveSingleItem().GetMessage().ShouldContain("reference it from a Given");
        emittedTheFeature(outcome).ShouldBeFalse();
    }

    [Fact]
    public void an_arrangement_that_includes_itself_is_refused()
    {
        const string cycle =
            """
              @arrangement
              Scenario: first
                Given second

              @arrangement
              Scenario: second
                Given first
            """;

        var outcome = run(feature(cycle, scenarioUsing("first")));

        outcome.WithId("BOBCAT022").ShouldNotBeEmpty();
        outcome.WithId("BOBCAT022").First().GetMessage().ShouldContain("includes itself");
        emittedTheFeature(outcome).ShouldBeFalse();
    }

    [Fact]
    public void an_arrangement_named_like_a_real_step_is_refused_because_a_reference_would_mean_two_things()
    {
        const string shadowing =
            """
              @arrangement
              Scenario: a wallet exists
                Given WalletOpened occurred
            """;

        var outcome = run(feature(shadowing, scenarioUsing("a wallet exists")));

        outcome.WithId("BOBCAT022").ShouldHaveSingleItem().GetMessage().ShouldContain("AWalletExists");
        emittedTheFeature(outcome).ShouldBeFalse();
    }

    [Fact]
    public void a_reference_carrying_a_table_is_refused_rather_than_dropping_it()
    {
        const string withTable =
            """
              Scenario: Crediting
                Given no events for Wallet "11111111-1111-1111-1111-111111111111"
                And an open wallet
                  | Owner |
                  | Ivy   |
            """;

        var outcome = run(feature(OpenWallet, withTable));

        outcome.WithId("BOBCAT022").ShouldHaveSingleItem().GetMessage().ShouldContain("would be dropped");
        emittedTheFeature(outcome).ShouldBeFalse();
    }

    [Fact]
    public void two_arrangements_with_one_name_are_refused()
    {
        var outcome = run(feature(OpenWallet, OpenWallet, scenarioUsing("an open wallet")));

        outcome.WithId("BOBCAT022").ShouldHaveSingleItem().GetMessage().ShouldContain("more than once");
        emittedTheFeature(outcome).ShouldBeFalse();
    }
}
