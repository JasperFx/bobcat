namespace Bobcat.Tests.Monitoring;

/// <summary>
/// <c>BOBCAT_MONITOR</c> is an environment variable, so it is one value for the whole test
/// process — and both <see cref="MonitorPublisherTests"/> and
/// <see cref="SpecEventModelPublishingTests"/> take ownership of it per test, clearing it in
/// their constructors and restoring it in <c>Dispose</c>. In separate collections xUnit runs
/// those two classes side by side, and each one's writes land in the other's run.
/// </summary>
/// <remarks>
/// The race is real in both directions, and both were reproduced by widening the window:
/// <list type="bullet">
/// <item><c>the_kill_switch_suppresses_even_the_probe</c> sets the switch to "0" mid-test, and a
/// concurrent <c>TryConnect</c> in the other class returns null — <c>publisher.ShouldNotBeNull()</c>
/// fails with "should not be null but was", which is how this surfaced: one failure in a
/// full-solution run on 2026-09-15, green when <c>Bobcat.Tests</c> ran alone.</item>
/// <item>The other class's constructor clears the switch while that same test is holding it, so
/// its own <c>TryConnect</c> hands back a live publisher and <c>ShouldBeNull()</c> fails instead.</item>
/// </list>
/// Serializing the two is what closes it; nothing here is slow enough for that to cost anything.
/// Parallelization is disabled rather than merely shared so that a future class calling
/// <c>TryConnect</c> cannot silently rejoin the race — the same call
/// <see cref="Bobcat.Tests.MarkerSteps.MarkerStepRunTests"/> makes for the same reason.
/// </remarks>
[CollectionDefinition("monitor-kill-switch", DisableParallelization = true)]
public class MonitorKillSwitchCollection;
