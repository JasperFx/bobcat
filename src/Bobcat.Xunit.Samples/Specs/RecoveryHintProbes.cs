using Bobcat.Resilience;

// Issue #469. The run-wide default for the probe failure: it clears in a fresh process. The
// classes below override it, one at class scope and one at method scope, so a supervisor driving
// this host sees all three scopes arrive through the spec manifest.
// #region hint-assembly
[assembly: ClearsInFreshProcess(typeof(Bobcat.Xunit.Samples.Specs.HintProbeException),
    Because = "the assembly-wide default for the recovery hint probes")]
// #endregion hint-assembly

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The failure the recovery hint probes throw. Its own type, so the hints declared for it reach
/// nothing else in this suite.
/// </summary>
public sealed class HintProbeException(string message) : Exception(message);

/// <summary>
/// Not a Storyteller sample: the targets <c>Bobcat.Supervisor.Tests</c> drives to prove a
/// supervisor honours the recovery hints a projected suite declares on itself (issue #469).
/// </summary>
/// <remarks>
/// Inert unless <see cref="StateVariable"/> names a directory, so a plain run of the samples sees
/// three green specifications and nothing about the samples' own outcomes moves. With it set, each probe
/// fails the way its name says, and a marker file per probe is how "the first attempt" survives
/// the fresh process a retry may run in.
/// </remarks>
internal static class HintProbe
{
    public const string StateVariable = "BOBCAT_HINT_PROBE";

    public static void FailsOnFirstAttempt(string probe)
    {
        if (Environment.GetEnvironmentVariable(StateVariable) is not { Length: > 0 } directory) return;

        var marker = Path.Combine(directory, probe);
        if (File.Exists(marker)) return;

        Directory.CreateDirectory(directory);
        File.WriteAllText(marker, "failed once");
        throw new HintProbeException($"{probe} fails on its first attempt");
    }

    public static void AlwaysFails(string probe)
    {
        if (Environment.GetEnvironmentVariable(StateVariable) is not { Length: > 0 }) return;
        throw new HintProbeException($"{probe} fails on every attempt");
    }
}

/// <summary>Declares nothing of its own, so the assembly-wide default applies.</summary>
/// <remarks>
/// Specifications rather than plain facts, because every test in this corpus is one:
/// <c>SpecIdentityEndToEndTests</c> holds the listing to the platform's own test count.
/// </remarks>
[BobcatFeature("Recovery hints, by the assembly default"), BobcatScenario]
public class AssemblyHintProbes
{
    [Fact]
    public void recovers_in_a_fresh_process_by_the_assembly_default()
        => HintProbe.FailsOnFirstAttempt(nameof(recovers_in_a_fresh_process_by_the_assembly_default));
}

/// <summary>A class that overrides the assembly default, and a method that overrides the class.</summary>
// #region hint-class-and-method
[BobcatFeature("Recovery hints, by class and method"), BobcatScenario]
[ClearsOnRetry(typeof(HintProbeException), Because = "the class-wide override for the recovery hint probes")]
public class ClassHintProbes
{
    [Fact]
    public void recovers_on_a_retry_by_the_class_override()
        => HintProbe.FailsOnFirstAttempt(nameof(recovers_on_a_retry_by_the_class_override));

    [Fact]
    [NeverRecovers(typeof(HintProbeException), Because = "the method-level override for the recovery hint probes")]
    public void never_recovers_by_the_method_override()
        => HintProbe.AlwaysFails(nameof(never_recovers_by_the_method_override));
}
// #endregion hint-class-and-method
