namespace Bobcat.Console.Runs;

/// <summary>
/// The one place a foreign framework's verdict is translated into the run's own outcome
/// vocabulary (issue #195). The supervisor forwards a worker's word verbatim — Passed, Failed,
/// Error, Skipped, Timeout, Cancelled — because re-labelling it at the publisher is how two
/// enums meaning the same thing quietly drift apart; the mapping belongs where the consuming
/// model lives, which is here and in the Pinia store's mirror of this rule.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Skipped counts as a clean pass</strong>, and that is a consistency rule rather than
/// a convenience: the supervisor's own <c>WorkerOutcome.Succeeded</c> is
/// <c>Passed or Skipped</c>, so a skipped test is already inside the <c>Passed</c> figure the
/// terminal <c>run_finished</c> carries. Calling it anything else here would leave the progress
/// bar and the final counts disagreeing about the same test. The framework's own word survives
/// on <see cref="ScenarioProjection.State"/> for anything that wants the distinction.
/// </para>
/// <para>
/// There is no mapping for Indeterminate: it never reaches the wire, because silence is not a
/// verdict and a padded outcome is not a live one. An unrecognised state — a framework word
/// this build has not met — is reported as a failure rather than dropped: a test that reached
/// SOME terminal state is done, and quietly not counting it would stall the progress bar for
/// the whole run, which is the exact failure this issue exists to remove.
/// </para>
/// </remarks>
public static class ForeignTestOutcome
{
    public const string CleanPass = "CleanPass";
    public const string Failed = "Failed";

    public static string From(string state) => state switch
    {
        "Passed" or "Skipped" => CleanPass,
        _ => Failed
    };
}
