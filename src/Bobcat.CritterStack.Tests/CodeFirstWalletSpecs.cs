using Bobcat.CodeFirst;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #170's acceptance vehicle: slice declarations authored in raw C#. Never registered with
/// a runner — what is under test is the <em>generator</em> reading these [Scenario] methods and
/// folding them into the same descriptor the .feature files feed, asserted by
/// <see cref="EventModelDescriptorTests"/>. Roles come from the typed steps a scenario body
/// names (here through <see cref="Specification.Host{TFixture}"/>-borrowed
/// <see cref="WalletFixture"/> steps); identity is {derived feature title}/{derived scenario
/// title}, the same string the runtime would publish on scenario_finished.
/// </summary>
public class WalletAuditSpecification : Specification
{
    private static readonly Guid id = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Scenario(Tags = ["slice:AuditWallet", "domain:Wallets"])]
    public void auditing_a_credited_wallet()
    {
        var wallet = Host<WalletFixture>();

        Given("an open wallet", () => wallet.GivenEvents<Wallet>(id, new WalletOpened(id, "Fay")));

        // Two commands: the arrange (open) and the act (debit). The slice's command must be the
        // LAST WhenCommand — the same last-When rule the Gherkin path applies.
        When("it is opened first", () => wallet.WhenCommand<Wallet>(new OpenWallet(id, "Fay")));
        When("the audit debit lands", () => wallet.WhenCommand<Wallet>(new DebitWallet(id, 1m)));

        Then("the debit event is there", () => wallet.ThenEvents(new WalletDebited(id, 1m)));
        Then("the summary caught up", () => wallet.ThenDocument<WalletSummary>(id, _ => { }));
        Then("the notification went out", () => wallet.ThenMessagesSent<WalletCreditedNotification>());
    }

    // Declared but unbound — the code-first form of the pending-specification hotspot: the
    // scenario exists on the model and nothing specifies it yet.
    [Scenario(Tags = ["slice:AuditWallet"])]
    public void auditing_an_empty_wallet()
    {
    }

    // Tagged into a slice the .feature file also feeds: one slice, fed by both authoring
    // styles, must come out as ONE descriptor with the union of their specifications.
    [Scenario(Tags = ["slice:CreditWallet"])]
    public void a_code_first_credit()
    {
        var wallet = Host<WalletFixture>();
        When("the credit lands", () => wallet.WhenCommand<Wallet>(new CreditWallet(id, 5m)));
        Then("it is emitted", () => wallet.ThenEvents(new WalletCredited(id, 5m)));
    }
}
