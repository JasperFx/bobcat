using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class FailureSignatureTests
{
    private sealed class BrokerUnavailableException() : TimeoutException("no broker");

    [Fact]
    public void an_in_process_signature_carries_the_whole_inheritance_chain()
    {
        // The chain is what lets a hint written against a base class match a derived failure.
        var signature = FailureSignature.FromException(new BrokerUnavailableException());

        signature.IsKnown.ShouldBeTrue();
        signature.TypeNames.ShouldContain(name => name.EndsWith("BrokerUnavailableException"));
        signature.TypeNames.ShouldContain("System.TimeoutException");
        signature.TypeNames.ShouldContain("System.Exception");
    }

    [Fact]
    public void the_chain_stops_short_of_object()
    {
        // object is not a failure class, and matching it would make every hint match everything.
        FailureSignature.FromException(new InvalidOperationException())
            .TypeNames.ShouldNotContain("System.Object");
    }

    [Fact]
    public void rank_orders_by_how_closely_a_type_describes_the_failure()
    {
        var signature = FailureSignature.FromException(new BrokerUnavailableException());

        // TimeoutException reaches Exception via SystemException, so the chain is four deep.
        signature.Rank(typeof(BrokerUnavailableException).FullName!).ShouldBe(0);
        signature.Rank("System.TimeoutException").ShouldBe(1);
        signature.Rank("System.SystemException").ShouldBe(2);
        signature.Rank("System.Exception").ShouldBe(3);
        signature.Rank("System.InvalidOperationException").ShouldBe(-1);
    }

    [Fact]
    public void a_simple_name_matches_a_qualified_one()
    {
        // A worker reports "TimeoutException"; the hint holds "System.TimeoutException". They are
        // the same failure, and refusing to match them would make hints useless out of process.
        FailureSignature.FromReportedType("TimeoutException", "timed out")
            .Matches("System.TimeoutException").ShouldBeTrue();

        FailureSignature.FromException(new TimeoutException())
            .Matches("TimeoutException").ShouldBeTrue();
    }

    [Fact]
    public void a_reported_type_has_no_chain_so_a_base_class_hint_does_not_match_it()
    {
        // The honest consequence of the wire carrying one name: over the boundary a hint must
        // name the exact type. It degrades to "no hint applied", never to a wrong retry.
        var signature = FailureSignature.FromReportedType("BrokerUnavailableException", "no broker");

        signature.Matches("BrokerUnavailableException").ShouldBeTrue();
        signature.Matches("System.TimeoutException").ShouldBeFalse();
    }

    [Fact]
    public void a_failure_whose_class_is_unknown_matches_nothing()
    {
        // tUnit erases exception types on the MTP wire entirely. That has to be representable.
        var signature = FailureSignature.FromReportedType(null, "it failed");

        signature.IsKnown.ShouldBeFalse();
        signature.Matches("System.Exception").ShouldBeFalse();
        signature.Message.ShouldBe("it failed");
    }

    [Fact]
    public void no_exception_is_no_signature()
    {
        FailureSignature.FromException(null).ShouldBeSameAs(FailureSignature.None);
        FailureSignature.None.IsKnown.ShouldBeFalse();
    }
}
