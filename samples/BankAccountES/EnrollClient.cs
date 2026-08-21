using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;

namespace BankAccountES;

public record EnrollClient(string Name, string Email)
{
    public class Validator : AbstractValidator<EnrollClient>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
        }
    }
}

public static class EnrollClientEndpoint
{
    // Created rather than a bare Client, so the response carries the 201 and Location a
    // newly-created resource should. Returned as a TypedResults value directly — a
    // (Client, IResult) tuple would be read by Wolverine.HTTP as (body, cascaded-message)
    // and the IResult dispatched as a message with no handler. See docs/sample-wiring.md
    // footgun 3.
    [WolverinePost("/api/clients")]
    public static Created<Client> Post(EnrollClient command, IDocumentSession session)
    {
        var clientId = Guid.NewGuid();
        var evt = new ClientEnrolled(clientId, command.Name, command.Email);

        session.Events.StartStream<Client>(clientId, evt);

        var client = new Client();
        client.Apply(evt);
        return TypedResults.Created($"/api/clients/{clientId}", client);
    }
}
