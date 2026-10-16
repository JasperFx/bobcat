using Bobcat;
using Bobcat.Xunit;
using Shouldly;
using Xunit;

namespace Compliance;

/// <summary>The per-transport half: Wolverine's TransportComplianceFixture, reduced to a transport.</summary>
public abstract class TransportComplianceFixture
{
    public abstract FakeTransport BuildTransport();
}

/// <summary>
/// The shape of Wolverine's <c>TransportCompliance&lt;T&gt;</c>: an abstract generic base in a class
/// library, whose test methods every transport project inherits through a bodyless subclass.
/// </summary>
public abstract class TransportCompliance<T> where T : TransportComplianceFixture, new()
{
    protected readonly T Fixture = new();
    protected readonly FakeTransport theTransport;

    /// <summary>
    /// The shared vocabulary lives on a NON-generic grammar class. On the generic base itself the
    /// generator emits an interceptor whose receiver is `TransportCompliance&lt;T&gt;` with no `T` in
    /// scope (CS0246), and Wolverine's own helpers are `protected`, which no interceptor can reach.
    /// </summary>
    protected readonly ComplianceSteps the;

    protected TransportCompliance()
    {
        theTransport = Fixture.BuildTransport();
        the = new ComplianceSteps(theTransport);
    }

    // ---- the battery -------------------------------------------------------------------------

    [BobcatSpec]
    public void can_send_from_one_node_to_another_by_destination()
    {
        the.AMessageIsSent();
        the.MessageEnds("succeeded", 1);
    }

    [BobcatSpec]
    public void tags_the_envelope_with_the_source()
    {
        the.AMessageIsSent();
        the.SourceIs("SenderService");
    }

    [BobcatSpec]
    public virtual void will_move_to_dead_letter_queue_without_any_exception_match()
    {
        the.HandlerThrowsOnAttempt(nameof(InvalidOperationException), 1);
        the.HandlerThrowsOnAttempt(nameof(InvalidOperationException), 2);
        the.HandlerThrowsOnAttempt(nameof(InvalidOperationException), 3);
        the.AMessageIsSent();
        the.MessageEnds("dead-lettered", 3);
    }

    [BobcatSpec]
    public virtual void will_move_to_dead_letter_queue_with_exception_match()
    {
        the.HandlerThrowsOnAttempt(nameof(DivideByZeroException), 1);
        the.AMessageIsSent();
        the.MessageEnds("dead-lettered", 1);
    }

    [BobcatSpec]
    public virtual void can_schedule_retry()
    {
        the.HandlerThrowsOnAttempt(nameof(BadImageFormatException), 1);
        the.AMessageIsSent();
        the.MessageEnds("succeeded", 2);
    }

    /// <summary>The marker-comment form, so the preview has declared steps to show.</summary>
    [BobcatSpec]
    public void will_requeue_and_increment_attempts()
    {
        // Given the handler throws DataMisalignedException on attempts 1 and 2
        the.HandlerThrowsOnAttempt(nameof(DataMisalignedException), 1);
        the.HandlerThrowsOnAttempt(nameof(DataMisalignedException), 2);

        // When a message is sent
        the.AMessageIsSent();

        // Then it succeeds on the third attempt
        the.MessageEnds("succeeded", 3);
    }

    /// <summary>Wolverine's early-return guard: a sender-only transport "passes" by doing nothing.</summary>
    [BobcatSpec]
    public void can_request_reply()
    {
        if (theTransport.IsSenderOnly) return;

        the.AMessageIsSent();
        the.MessageEnds("succeeded", 1);
    }

    /// <summary>A plain [Fact] beside the [BobcatSpec]s: what an unconverted test looks like.</summary>
    [Fact]
    public void all_listeners_say_they_are_accepting_on_startup()
        => theTransport.Scheme.ShouldNotBeNullOrEmpty();
}
