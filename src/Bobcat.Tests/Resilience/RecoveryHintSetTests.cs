using JasperFx.Testing;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

public class RecoveryHintSetTests
{
    private sealed class BrokerUnavailableException() : TimeoutException("no broker");

    [ClearsOnRetry(typeof(TimeoutException), Because = "the broker is slow to warm up")]
    [ClearsOnRecycle("rabbit,kafka", typeof(BrokerUnavailableException))]
    [NeverRecovers(typeof(NotSupportedException))]
    private sealed class HintedFixture;

    private sealed class UnhintedFixture;

    private static FailureSignature failure<T>() where T : Exception, new()
        => FailureSignature.FromException(new T());

    [Fact]
    public void hints_are_read_off_a_fixture_with_the_authors_rationale_intact()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Hints.Count.ShouldBe(3);

        var retry = hints.Hints.Single(h => h.Kind == DispositionKind.RetryInProcess);
        retry.FailureTypeName.ShouldBe("System.TimeoutException");
        retry.Because.ShouldBe("the broker is slow to warm up");
        retry.Source.ShouldBe(nameof(HintedFixture));
        retry.Scope.ShouldBe(HintScope.Group);
    }

    [Fact]
    public void a_recycle_hint_carries_every_named_resource()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Hints.Single(h => h.Kind == DispositionKind.RetryAfterRecycle)
            .Resources.ShouldBe(["rabbit", "kafka"]);
    }

    [Fact]
    public void a_type_with_no_hints_contributes_none()
    {
        new RecoveryHintSet().AddFromType(typeof(UnhintedFixture)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void the_best_hint_for_a_derived_failure_is_the_one_naming_it_exactly()
    {
        // Both hints match a BrokerUnavailableException — it IS a TimeoutException. The author
        // who named the exact type knew more, so recycling wins over a plain retry.
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Best("any/test", failure<BrokerUnavailableException>())!
            .Kind.ShouldBe(DispositionKind.RetryAfterRecycle);
    }

    [Fact]
    public void a_base_class_hint_still_catches_a_failure_nothing_names_exactly()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Best("any/test", failure<TimeoutException>())!.Kind.ShouldBe(DispositionKind.RetryInProcess);
    }

    [Fact]
    public void an_undescribed_failure_has_no_best_hint()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Best("any/test", failure<InvalidOperationException>()).ShouldBeNull();
    }

    [Fact]
    public void a_narrower_scope_overrides_a_wider_one_even_on_a_less_specific_type()
    {
        // The declaration closest to the test has the last word — the same override rule Bobcat
        // uses everywhere. A fixture can veto a run-wide default without knowing what it names.
        var hints = new RecoveryHintSet()
            .Add(new RecoveryHint
            {
                FailureTypeName = typeof(BrokerUnavailableException).FullName!,
                Kind = DispositionKind.RetryInProcess,
                Scope = HintScope.Global,
                Source = "assembly"
            })
            .Add(new RecoveryHint
            {
                FailureTypeName = "System.TimeoutException",
                Kind = DispositionKind.FailAndContinue,
                Scope = HintScope.Group,
                Source = "TheFixture"
            });

        hints.Best("any/test", failure<BrokerUnavailableException>())!.Source.ShouldBe("TheFixture");
    }

    [Fact]
    public void a_hint_only_applies_to_the_tests_it_was_scoped_to()
    {
        // A fixture's hints must not silence retries in some other feature.
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture), "Orders/");

        hints.Best("Orders/places an order", failure<TimeoutException>()).ShouldNotBeNull();
        hints.Best("Shipping/ships it", failure<TimeoutException>()).ShouldBeNull();
    }

    [Fact]
    public void a_hint_with_no_prefix_applies_everywhere()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Best("Shipping/ships it", failure<TimeoutException>()).ShouldNotBeNull();
    }

    [Fact]
    public void a_failure_of_unknown_class_matches_no_hint()
    {
        var hints = new RecoveryHintSet().AddFromType(typeof(HintedFixture));

        hints.Best("any/test", FailureSignature.FromReportedType(null, "it failed")).ShouldBeNull();
    }

    [Fact]
    public void a_recycle_hint_naming_no_resource_is_rejected_when_it_is_declared()
    {
        // Recycling nothing is just a retry. Accepting it would report a recycle that never
        // happened, which is the one thing the reporting rules forbid.
        var thrown = Should.Throw<InvalidOperationException>(() => new RecoveryHintSet().Add(new RecoveryHint
        {
            FailureTypeName = "System.TimeoutException",
            Kind = DispositionKind.RetryAfterRecycle,
            Source = "TheFixture"
        }));

        thrown.Message.ShouldContain("names no resources");
        thrown.Message.ShouldContain("TheFixture");
    }

    [Fact]
    public void a_hint_cannot_declare_a_disposition_that_is_not_about_recovery()
    {
        Should.Throw<InvalidOperationException>(() => new RecoveryHintSet().Add(new RecoveryHint
        {
            FailureTypeName = "System.TimeoutException",
            Kind = DispositionKind.AbortRun
        })).Message.ShouldContain("cannot declare AbortRun");
    }

    [NeverRecovers(typeof(string))]
    private sealed class BadlyHintedFixture;

    [Fact]
    public void a_hint_naming_something_that_is_not_an_exception_says_so_by_name()
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => new RecoveryHintSet().AddFromType(typeof(BadlyHintedFixture)));

        thrown.Message.ShouldContain("String");
        thrown.Message.ShouldContain(nameof(BadlyHintedFixture));
    }

    [Fact]
    public void a_hint_describes_itself_the_way_the_report_will_print_it()
    {
        var hint = new RecoveryHintSet().AddFromType(typeof(HintedFixture)).Hints
            .Single(h => h.Kind == DispositionKind.RetryInProcess);

        hint.ToString().ShouldBe(
            "TimeoutException clears on retry (declared on HintedFixture): the broker is slow to warm up");
    }
}
