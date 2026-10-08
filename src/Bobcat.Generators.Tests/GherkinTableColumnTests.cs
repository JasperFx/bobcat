using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT032 in the shipped Gherkin grammar (bobcat#420): the type comes from the step's capture,
/// so a column naming no member of it is a build error — Gherkin's answer to <c>nameof</c>.
/// </summary>
public class GherkinTableColumnTests
{
    private const string Source =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Bobcat.CritterStack;

        namespace Specs;

        public record WalletOpened(Guid WalletId, string Owner);
        public record OpenWallet(Guid WalletId, string Owner);
        public record Deposited(Guid WalletId, decimal Amount);
        public class Wallet { }

        [FixtureTitle("Wallet")]
        public class WalletFixture : CritterStackFixture
        {
            protected override Task<IActOutcome> DispatchAsync(object command, int timeoutInMilliseconds)
                => throw new NotImplementedException();
        }
        """;

    private static GeneratorHarness.RunOutcome run(string steps)
        => GeneratorHarness.Run(Source, ("Wallet.feature", $"""
                                                          Feature: Wallet

                                                            Scenario: Opening
                                                          {steps}
                                                          """));

    private static IReadOnlyList<string> messages(GeneratorHarness.RunOutcome outcome)
        => outcome.WithId("BOBCAT032").Select(d => d.GetMessage()).ToList();

    [Fact]
    public void valid_columns_are_clean()
    {
        var outcome = run("""
                              Given WalletOpened occurred
                                | walletId                             | Owner |
                                | 11111111-1111-1111-1111-111111111111 | Ann   |
                              When OpenWallet is received
                                | WalletId                             | Owner |
                                | 11111111-1111-1111-1111-111111111111 | Ann   |
                              Then WalletOpened is emitted
                                | Owner |
                                | Ann   |
                          """);

        messages(outcome).ShouldBeEmpty();
    }

    [Fact]
    public void a_typo_under_an_event_capture_is_an_error()
    {
        var outcome = run("""
                              Given no events for Wallet "11111111-1111-1111-1111-111111111111"
                              When OpenWallet is received
                                | WalletId                             | Owner |
                                | 11111111-1111-1111-1111-111111111111 | Ann   |
                              Then WalletOpened is emitted
                                | Ownr |
                                | Ann  |
                          """);

        var message = messages(outcome).ShouldHaveSingleItem();
        message.ShouldContain("'Ownr' does not match WalletOpened");
        message.ShouldContain("Owner");
    }

    [Fact]
    public void a_typo_under_a_command_capture_is_an_error()
    {
        messages(run("""
                          When OpenWallet is received
                            | WalletId                             | Name |
                            | 11111111-1111-1111-1111-111111111111 | Ann  |
                      """)).ShouldHaveSingleItem().ShouldContain("'Name' does not match OpenWallet");
    }

    [Fact]
    public void a_vertical_table_checks_its_fields()
    {
        messages(run("""
                          Given WalletOpened occurred
                            | field | value |
                            | Ownr  | Ann   |
                      """)).ShouldHaveSingleItem().ShouldContain("'Ownr'");
    }

    [Fact]
    public void events_for_checks_each_row_against_its_own_event_type()
    {
        var outcome = run("""
                              Given events for Wallet
                                | Event        | WalletId                             | Owner | Amount |
                                | WalletOpened | 11111111-1111-1111-1111-111111111111 | Ann   |        |
                                | Deposited    | 11111111-1111-1111-1111-111111111111 |       | 10     |
                          """);

        messages(outcome).ShouldBeEmpty();

        var typo = run("""
                           Given events for Wallet
                             | Event     | WalletId                             | Amont |
                             | Deposited | 11111111-1111-1111-1111-111111111111 | 10    |
                       """);

        messages(typo).ShouldHaveSingleItem().ShouldContain("'Amont' has a value for a Deposited");
    }

    [Fact]
    public void the_event_group_assertions_check_each_row_against_its_own_event_type()
    {
        var exactly = run("""
                              When OpenWallet is received
                                | WalletId                             | Owner |
                                | 11111111-1111-1111-1111-111111111111 | Ann   |
                              Then exactly these events are emitted
                                | Event        | Ownr |
                                | WalletOpened | Ann  |
                          """);

        messages(exactly).ShouldHaveSingleItem().ShouldContain("'Ownr'");

        var anyOrder = run("""
                               When OpenWallet is received
                                 | WalletId                             | Owner |
                                 | 11111111-1111-1111-1111-111111111111 | Ann   |
                               Then these events are emitted in any order
                                 | Event        | Owner |
                                 | WalletOpened | Ann   |
                           """);

        messages(anyOrder).ShouldBeEmpty();
    }

    [Fact]
    public void not_emitted_checks_its_rows_against_the_captured_type()
    {
        var outcome = run("""
                              When OpenWallet is received
                                | WalletId                             | Owner |
                                | 11111111-1111-1111-1111-111111111111 | Ann   |
                              Then WalletOpened is not emitted
                                | Ownr |
                                | Bob  |
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("'Ownr'");
    }
}
