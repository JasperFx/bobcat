using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// Issue #259: naming the arranged event in the step text rather than in a table cell moves its
/// resolution from run time to build time.
/// </summary>
/// <remarks>
/// This is the part of the readability change that is not about reading. With
/// <c>Given events for {aggregate}</c> the event type is a value in an <c>Event</c> column,
/// resolved by <c>EventTypeResolver</c> when the scenario runs — so a misspelling is a failing
/// test. With <c>Given {event} occurred</c> it is a capture the generator resolves against the
/// compilation, so the same misspelling is BOBCAT011 before anything runs.
/// </remarks>
public class PerEventArrangeTests
{
    private const string Fixture =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;

        namespace Specs;

        public record WalletOpened(Guid WalletId, string Owner);

        [FixtureTitle("Wallet")]
        public class WalletFixture : Fixture
        {
            [Given("no events for {aggregate} {string}")]
            public void GivenNoEvents(Type aggregate, string id) { }

            [Given("{event} occurred")]
            public Task GivenEventOccurred(Type @event, StepTable? fields) => Task.CompletedTask;
        }

        public class Wallet { }
        """;

    private static string feature(string eventName) =>
        $"""
         Feature: Wallet

           Scenario: Prior history
             Given no events for Wallet "11111111-1111-1111-1111-111111111111"
             And {eventName} occurred
         """;

    [Fact]
    public void a_correctly_named_arranged_event_resolves_at_build_time()
    {
        var outcome = GeneratorHarness.Run(Fixture, ("Wallet.feature", feature("WalletOpened")));

        outcome.WithId("BOBCAT011").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();

        // Resolved to a typeof(...) against the compilation, not left as a string for run time.
        outcome.GeneratedSource("Wallet").ShouldContain("typeof(global::Specs.WalletOpened)");
    }

    [Fact]
    public void a_misspelled_arranged_event_is_a_build_error_not_a_failing_scenario()
    {
        var outcome = GeneratorHarness.Run(Fixture, ("Wallet.feature", feature("WalletOpenned")));

        var diagnostic = outcome.WithId("BOBCAT011").ShouldHaveSingleItem();
        diagnostic.GetMessage().ShouldContain("WalletOpenned");
    }

    [Fact]
    public void the_arranged_event_needs_no_table_because_a_field_less_event_is_a_real_one()
    {
        // new AppointmentArchived() is a perfectly good prior fact, and an emlang import carries no
        // field information at all — so a scaffolded arrange routinely has nothing to tabulate.
        var outcome = GeneratorHarness.Run(Fixture, ("Wallet.feature", feature("WalletOpened")));

        outcome.WithId("BOBCAT020").ShouldBeEmpty();
        outcome.CompilationErrors.ShouldBeEmpty();
    }
}
