using Shouldly;
using Xunit;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// Issue #404: a projected spec that exists before its behaviour does. The runtime half — the
/// generator half, which turns it into a <c>PendingSpecification</c> hotspot, is in
/// <c>Bobcat.Generators.Tests/PendingProjectedSpecTests</c>.
/// </summary>
[BobcatFeature("Pending specifications")]
public class PendingSpecTests
{
    /// <summary>
    /// The spec stub-first work actually writes. It is SKIPPED, so the throw never happens — and
    /// a suite in this state is green with a skip, rather than red in a way nothing distinguishes
    /// from a real failure.
    /// </summary>
    [BobcatSpec(Pending = true)]
    public void a_pending_specification_is_never_run()
        => throw new NotImplementedException(
            "A scaffolded skeleton throws. If this ever runs, Pending stopped skipping and the "
            + "whole point of #404 is gone.");

    [Fact]
    public void pending_sets_the_skip_reason_xunit_reports()
    {
        var pending = new BobcatSpecAttribute { Pending = true };

        pending.Pending.ShouldBeTrue();
        pending.Skip.ShouldBe(BobcatSpecAttribute.PendingSkipReason);

        // Skipped, never swallowed: the reason says what it is waiting for and how to clear it,
        // because a bare "Skipped" is how a stub outlives its stub phase.
        pending.Skip.ShouldContain("Remove Pending = true");
    }

    [Fact]
    public void a_spec_that_is_not_pending_is_not_skipped()
    {
        new BobcatSpecAttribute().Skip.ShouldBeNull();
        new BobcatSpecAttribute { Pending = false }.Skip.ShouldBeNull();
    }

    [Fact]
    public void an_explicit_skip_reason_wins_in_either_order()
    {
        // Property initializers run in written order, so both orders have to be checked rather
        // than one of them reasoned about.
        new BobcatSpecAttribute { Pending = true, Skip = "waiting on the broker" }
            .Skip.ShouldBe("waiting on the broker");

        new BobcatSpecAttribute { Skip = "waiting on the broker", Pending = true }
            .Skip.ShouldBe("waiting on the broker");
    }

    [Fact]
    public void the_pending_marker_composes_with_a_slice_binding()
    {
        var pending = new BobcatSpecAttribute(typeof(PendingSpecTests)) { Pending = true };

        pending.SliceType.ShouldBe(typeof(PendingSpecTests));
        pending.Skip.ShouldBe(BobcatSpecAttribute.PendingSkipReason);
    }
}
