using Bobcat;

namespace Compliance;

/// <summary>The compliance battery's step vocabulary, shared by every transport.</summary>
public class ComplianceSteps(FakeTransport transport)
{
    private readonly Dictionary<int, Exception> _failures = new();
    private Delivery? _delivery;

    [Given("the handler throws {exception} on attempt {attempt}")]
    public void HandlerThrowsOnAttempt(string exception, int attempt)
        => _failures[attempt] = exception switch
        {
            nameof(InvalidOperationException) => new InvalidOperationException(),
            nameof(DivideByZeroException) => new DivideByZeroException(),
            nameof(DataMisalignedException) => new DataMisalignedException(),
            nameof(BadImageFormatException) => new BadImageFormatException(),
            _ => throw new ArgumentOutOfRangeException(nameof(exception))
        };

    [When("a message is sent")]
    public void AMessageIsSent()
        => _delivery = transport.Send(new Message1(Guid.NewGuid()), a => _failures.GetValueOrDefault(a));

    [Then("the message ends {outcome} on attempt {attempt}")]
    public void MessageEnds(string outcome, int attempt)
    {
        SpecAssert.Check("outcome", _delivery!.Outcome, outcome);
        SpecAssert.Check("attempt", _delivery.Attempt, attempt);
    }

    [Then("the envelope's source is {source}")]
    public void SourceIs(string source) => SpecAssert.Check("source", _delivery!.Source, source);
}
