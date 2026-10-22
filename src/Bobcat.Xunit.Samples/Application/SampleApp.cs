namespace Bobcat.Xunit.Samples.Scenarios;

// The system behind SampleScenario, the docs' example of a scenario vocabulary of your own: an in-memory
// event store, a document store, and the handlers three slices need. Its own namespace, because the
// tutorial's Application.Account is a different thing with the same name.

public interface IDocument
{
    Guid Id { get; }
}

public record Account;
public record AccountOpened(Guid Id, string Owner);
public record AccountStarted(Guid Id, Guid ClientId);
public record Deposit(Guid AccountId, decimal Amount);
public record FundsDeposited(Guid AccountId, decimal Amount);
public record DepositReceipt(Guid AccountId, decimal Amount);

public record Client;
public record ClientEnrolled(Guid Id, string Name);
public record OpenAccount(Guid ClientId);

public record Passenger(Guid Id, string Name) : IDocument;
public record Flight(Guid Id, string Number) : IDocument;
public record Booking(Guid Id, Guid PassengerId, Guid FlightId) : IDocument;
public record CreateBooking(Guid PassengerId, Guid FlightId);

public class SampleApp
{
    private readonly Dictionary<Guid, List<object>> _streams = new();
    private readonly Dictionary<(Type, Guid), object> _documents = new();

    /// <summary>What the last act appended, sent and started.</summary>
    public List<object> Appended { get; } = new();
    public List<object> Sent { get; } = new();
    public List<Type> Started { get; } = new();

    public void Append(Guid id, IEnumerable<object> events)
    {
        if (!_streams.TryGetValue(id, out var stream)) _streams[id] = stream = new();
        stream.AddRange(events);
    }

    public void Store(IDocument document) => _documents[(document.GetType(), document.Id)] = document;

    public Task Handle(object command)
    {
        Appended.Clear();
        Sent.Clear();
        Started.Clear();

        switch (command)
        {
            case Deposit deposit:
                append(deposit.AccountId, new FundsDeposited(deposit.AccountId, deposit.Amount));
                Sent.Add(new DepositReceipt(deposit.AccountId, deposit.Amount));
                break;

            case OpenAccount open:
                // The lookup: the handler checks the client exists, and decides nothing about it
                if (!_streams.ContainsKey(open.ClientId)) throw new InvalidOperationException("no such client");

                var accountId = Guid.NewGuid();
                append(accountId, new AccountStarted(accountId, open.ClientId));
                Started.Add(typeof(Account));
                break;

            case CreateBooking create:
                if (!_documents.ContainsKey((typeof(Passenger), create.PassengerId))) throw new InvalidOperationException("no such passenger");
                if (!_documents.ContainsKey((typeof(Flight), create.FlightId))) throw new InvalidOperationException("no such flight");

                Store(new Booking(Guid.NewGuid(), create.PassengerId, create.FlightId));
                break;

            default:
                throw new InvalidOperationException($"no handler for {command.GetType().Name}");
        }

        return Task.CompletedTask;
    }

    private void append(Guid id, object @event)
    {
        Append(id, [@event]);
        Appended.Add(@event);
    }
}
