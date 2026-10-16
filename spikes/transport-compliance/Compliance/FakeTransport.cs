namespace Compliance;

/// <summary>
/// A stand-in for a Wolverine transport: just enough behaviour for the compliance base's steps to say
/// something true or false. The spike is about how the specifications RENDER and are IDENTIFIED across
/// subclasses, not about messaging, so no broker is involved.
/// </summary>
public class FakeTransport
{
    private readonly List<Delivery> _deliveries = new();

    public required string Scheme { get; init; }
    public bool SupportsScheduledRetry { get; init; } = true;
    public bool IsSenderOnly { get; init; }
    public int MaximumAttempts { get; init; } = 3;

    public IReadOnlyList<Delivery> Deliveries => _deliveries;

    public Delivery Send(object message, Func<int, Exception?> failureOnAttempt)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            var failure = failureOnAttempt(attempt);
            if (failure is null) return record(message, "succeeded", attempt);

            if (failure is DivideByZeroException) return record(message, "dead-lettered", attempt);
            if (failure is BadImageFormatException && !SupportsScheduledRetry) return record(message, "dead-lettered", attempt);
            if (attempt >= MaximumAttempts) return record(message, "dead-lettered", attempt);
        }
    }

    private Delivery record(object message, string outcome, int attempt)
    {
        var delivery = new Delivery(message, outcome, attempt, "SenderService");
        _deliveries.Add(delivery);
        return delivery;
    }
}

public record Delivery(object Message, string Outcome, int Attempt, string Source);

public record Message1(Guid Id);
