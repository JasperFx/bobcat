using System.Runtime.CompilerServices;

namespace Bobcat.Xunit.Tests;

/// <summary>
/// This suite opts out of the projected spec report.
/// </summary>
/// <remarks>
/// <para>
/// A projected run prints its specifications by default in a terminal with nothing listening on the
/// wire (issue #384), and this is the one project in the repository where that is wrong: it drives
/// <c>MarkerStepRun</c> for real in order to <em>test</em> the adapter, so its recordings are
/// fixtures rather than specifications anybody wants to read. Left on, a green adapter suite ended
/// with a three-specification report, two of them "declares no steps".
/// </para>
/// <para>
/// A module initializer, because the default is taken when the first projected scenario opens its
/// bracket — later than any test's constructor, and later than anything a <c>[BeforeAll]</c> could
/// do. Setting the variable here is read by both the explicit and the default path, so one line turns
/// it off for the whole process.
/// </para>
/// </remarks>
internal static class NoSpecConsole
{
    [ModuleInitializer]
    internal static void SuppressTheReport()
        => Environment.SetEnvironmentVariable(ProjectedSpecConsole.EnvironmentVariable, "0");
}
