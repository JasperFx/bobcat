using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace BankAccountES;

public record OpenAccount(Guid ClientId, string Currency = "USD")
{
    public class Validator : AbstractValidator<OpenAccount>
    {
        public Validator()
        {
            RuleFor(x => x.ClientId).NotEmpty();
            RuleFor(x => x.Currency).NotEmpty().Length(3);
        }
    }
}

public static class OpenAccountEndpoint
{
    // The [Entity] load is the interesting half: opening an account for a client that was never
    // enrolled is a 400 from the middleware, before this method runs at all.
    [WolverinePost("/api/accounts")]
    public static Created<Account> Post(
        OpenAccount command,
        [Entity("ClientId", Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Client client,
        IDocumentSession session)
    {
        var accountId = Guid.NewGuid();
        var evt = new AccountOpened(accountId, command.ClientId, command.Currency);

        session.Events.StartStream<Account>(accountId, evt);

        var account = new Account();
        account.Apply(evt);
        return TypedResults.Created($"/api/accounts/{accountId}", account);
    }
}
