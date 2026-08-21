using FluentValidation;
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
    // enrolled is a 400 from the middleware, before this method runs at all. [Entity] is Wolverine's
    // store-agnostic document load, so it reads the Client snapshot from whichever store Program.cs
    // registered.
    //
    // The write is a Storage.StartStream() side effect rather than a call on an injected session:
    // the endpoint stays a pure function of its inputs, and Wolverine appends the stream through the
    // registered event store — Marten or Fisher — inside the same transaction as the response.
    [WolverinePost("/api/accounts")]
    public static (Created<Account>, StartStream) Post(
        OpenAccount command,
        [Entity("ClientId", Required = true, OnMissing = OnMissing.ProblemDetailsWith400)] Client client)
    {
        var accountId = Guid.NewGuid();
        var evt = new AccountOpened(accountId, command.ClientId, command.Currency);

        var account = new Account();
        account.Apply(evt);
        return (
            TypedResults.Created($"/api/accounts/{accountId}", account),
            Storage.StartStream<Account>(accountId, evt));
    }
}
