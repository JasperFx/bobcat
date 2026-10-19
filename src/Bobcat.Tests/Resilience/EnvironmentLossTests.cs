using Bobcat.Engine;
using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Tests.Resilience;

/// <summary>
/// The shape of bobcat#539's halt. The halt itself is process-wide, so it is proven against a real
/// host per adapter in a child process (each adapter's <c>EnvironmentLossTests</c>, and
/// <c>TUnitHaltTests</c>), never here, where it would stop this suite's own run.
/// </summary>
public class EnvironmentLossTests
{
    [Fact]
    public void a_loss_in_a_scenario_names_the_scenario()
        => new EnvironmentLoss("Checkout/pays by card", typeof(SpecCatastrophicException).FullName!, "the database is gone")
            .Describe().ShouldBe("Environment lost in Checkout/pays by card: the database is gone");

    [Fact]
    public void a_loss_in_setup_names_no_scenario()
        => new EnvironmentLoss(null, typeof(SpecCatastrophicException).FullName!, "the app did not start")
            .Describe().ShouldBe("Environment lost: the app did not start");

    [Fact]
    public void a_halted_scenario_says_it_did_not_run_and_carries_no_inner_exception()
    {
        var halted = new SpecEnvironmentLostException(
            new EnvironmentLoss(null, typeof(SpecCatastrophicException).FullName!, "the app did not start"));

        halted.Message.ShouldBe("Environment lost: the app did not start (this scenario did not run)");

        // One startup stack trace, on the test that met it, rather than one per halted test.
        halted.InnerException.ShouldBeNull();
    }

    [Fact]
    public void a_halted_scenario_is_never_retried()
    {
        // It is catastrophic itself, so a retry policy refuses it for the reason it refuses the original.
        var halted = new SpecEnvironmentLostException(
            new EnvironmentLoss(null, typeof(SpecCatastrophicException).FullName!, "the app did not start"));

        new DefaultFailurePolicy().Decide(new AttemptContext
        {
            TestId = "Feature/Scenario",
            Title = "Scenario",
            AttemptNumber = 1,
            Succeeded = false,
            FailureLevel = FailureLevel.Critical,
            Exception = halted,
            RetriesAvailable = true,
            AttemptsAllowed = 3,
            Traits = TestTraits.Of("Retry=3")
        })!.Kind.ShouldBe(DispositionKind.AbortRun);
    }
}
