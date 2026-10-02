using Bobcat.Runtime;

namespace Bobcat.Residency;

/// <summary>
/// The suite a <see cref="ResidentRunner"/> speaks for: what it specifies, how it can be asked to
/// run, and how it actually runs a selection (issue #390).
/// </summary>
/// <remarks>
/// <para>
/// <b>The seam is here because only the lane knows how to run anything.</b> A Gherkin suite is a
/// Bobcat host, so it runs a selection against its own <c>BobcatRunner</c> in process. A projected
/// suite's test framework owns the process, so it runs one by launching a filtered child. The
/// protocol above does not change between them, which is the point of issue #391 — the runner
/// receives identities and nothing more.
/// </para>
/// <para>
/// It is the same shape of decision as <c>IWorkerClient</c> in the supervisor: everything the
/// protocol knows stays on one side of this line, and everything a particular runner knows stays
/// on the other.
/// </para>
/// </remarks>
public interface IResidentSuite
{
    /// <summary>The suite's name, the same string <c>run_started</c> reports.</summary>
    string Suite { get; }

    /// <summary><c>gherkin</c> or <c>projected</c>.</summary>
    string Lane { get; }

    /// <summary>
    /// The modes this suite can honour. <see cref="RunnerWire.ColdMode"/> is always one of them;
    /// <see cref="RunnerWire.WarmMode"/> only for a lane that can keep a host booted.
    /// </summary>
    IReadOnlyList<string> Modes { get; }

    /// <summary>
    /// Every specification identity, as issue #391's manifest lists them. Read once at
    /// registration, so it describes the code this process was built from — which is exactly
    /// right, since a source change restarts the runner.
    /// </summary>
    IReadOnlyList<string> SpecIdentities { get; }

    /// <summary>
    /// Run the selection, in the mode asked for, publishing on the ingest stream as any run does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command id reaches the run's <c>run_started</c> through <c>BOBCAT_RUN_COMMAND</c>
    /// (issue #392), so a viewer can follow its own button press to the run it produced.
    /// </para>
    /// <para>
    /// A run that fails is not an error here: red is an outcome, and it travels on the ingest
    /// stream like any other. Throwing from this means the <i>suite</i> could not be run at all,
    /// and <see cref="ResidentRunner"/> reports that without letting it end the runner.
    /// </para>
    /// </remarks>
    Task Run(string commandId, SpecSelection selection, string mode, CancellationToken token);

    /// <summary>
    /// Why <see cref="RunnerWire.WarmMode"/> is no longer on offer, or null while it still is
    /// (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Answered once a warm run has left the host damaged — a reset that threw, a teardown that
    /// blew up. The runner then withdraws <c>warm</c> from the modes it registers and refuses a
    /// warm command <i>with this reason</i>, rather than running the next one on a poisoned host.
    /// </para>
    /// <para>
    /// It is specifically about the mode and not about the suite, because <b>cold is unaffected</b>:
    /// a cold command builds a new runner over fresh resources, so whatever poisoned the warm host
    /// is exactly what cold starts over from. A suite that could not run anything at all would
    /// have nothing to report here — it would fail the command and say so.
    /// </para>
    /// </remarks>
    string? WarmUnavailable => null;
}
