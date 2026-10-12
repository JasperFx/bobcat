namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The tests that drive a real Bobcat.TUnit.Tests child host. They are in CI again (bobcat#521),
/// and <c>BOBCAT_SKIP_TUNIT_HOSTED=1</c> is now only an opt-out for anyone who needs one — nothing
/// in the build sets it. <c>./build.sh TUnitTests</c> runs these two suites and nothing else, which
/// is how they get debugged.
/// </summary>
/// <remarks>
/// Why they were out: the 0.60.2 publish job failed on
/// <c>TUnitResidentSuiteTests.a_cold_command_runs_exactly_the_tunit_specifications_it_names</c> in
/// both attempts, with "the child never closed its run", while tests.yml passed on the same commit.
/// The difference the issue named was Nuke ordering <c>Pack</c> between Compile and Test, where its
/// second <c>dotnet pack</c> (no <c>--no-build</c>) rebuilt core, the supervisor and the tool just
/// before the tests ran; <c>Pack</c> is now <c>.After(Test)</c>. And the failure said nothing it
/// could have said: the child's exit code and output were captured by the suite and thrown away by
/// the test, which now carries them into every timeout message.
/// </remarks>
internal static class TUnitHosted
{
    public const string Variable = "BOBCAT_SKIP_TUNIT_HOSTED";

    public static bool Excluded => Environment.GetEnvironmentVariable(Variable) == "1";

    public const string SkipReason =
        "BOBCAT_SKIP_TUNIT_HOSTED=1 left the TUnit-hosted tests out of this run (bobcat#521); "
        + "./build.sh TUnitTests runs them";
}

/// <summary>A <see cref="FactAttribute"/> that skips under <c>BOBCAT_SKIP_TUNIT_HOSTED=1</c> (bobcat#521).</summary>
public sealed class TUnitHostedFactAttribute : FactAttribute
{
    public TUnitHostedFactAttribute()
    {
        if (TUnitHosted.Excluded) Skip = TUnitHosted.SkipReason;
    }
}

/// <summary>A <see cref="TheoryAttribute"/> that skips under <c>BOBCAT_SKIP_TUNIT_HOSTED=1</c> (bobcat#521).</summary>
public sealed class TUnitHostedTheoryAttribute : TheoryAttribute
{
    public TUnitHostedTheoryAttribute()
    {
        if (TUnitHosted.Excluded) Skip = TUnitHosted.SkipReason;
    }
}
