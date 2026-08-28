using FluentValidation;
using Wolverine.Persistence.EventSourcing;

namespace BankAccountES;

public record AccountFrozen(Guid AccountId, string Reason);

/// <summary>
/// ⚠️ The PLANTED DISAGREEMENT for the four-source event-model vehicle (bobcat#172). The freeze
/// handler emits this event and the Gherkin spec deliberately does not mention it, so the
/// Declared claim on the FreezeAccount slice's emitted events ([AccountFrozen]) loses to the
/// Derived one ([AccountFrozen, AccountFlagged]) and the merge records a SourceDisagreement
/// hotspot (jasperfx#704) instead of silently swallowing the difference. Do not "fix" the spec
/// to mention this event — the gap is the point.
/// </summary>
public record AccountFlagged(Guid AccountId, string Reason);

public record FreezeAccount(Guid AccountId, string Reason)
{
    public class Validator : AbstractValidator<FreezeAccount>
    {
        public Validator()
        {
            RuleFor(x => x.AccountId).NotEmpty();
            RuleFor(x => x.Reason).NotEmpty();
        }
    }
}

/// <summary>
/// The one message-handled slice in the sample — the fraud desk freezes an account over the bus
/// rather than over HTTP, which is what lets the Bobcat grammar's <c>When FreezeAccount is
/// received</c> dispatch it with a tracked Wolverine session. Same store-agnostic decider shape
/// as the HTTP endpoints: load the <see cref="Account"/>, return the events to append.
/// </summary>
public static class FreezeAccountHandler
{
    [DeciderFunction]
    public static (AccountFrozen, AccountFlagged) Handle(FreezeAccount command, Account account)
        => (new AccountFrozen(command.AccountId, command.Reason),
            new AccountFlagged(command.AccountId, $"Frozen: {command.Reason}"));
}
