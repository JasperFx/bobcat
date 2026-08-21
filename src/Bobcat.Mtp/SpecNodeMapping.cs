using System.Text;
using Bobcat.Engine;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Microsoft.Testing.Platform.Extensions.Messages;

namespace Bobcat.Mtp;

/// <summary>
/// Projects Bobcat's model onto Microsoft.Testing.Platform's. Pure and static so the mapping
/// can be tested without standing a test host up.
/// </summary>
public static class SpecNodeMapping
{
    /// <summary>
    /// The stable identity for a scenario, used as the MTP test node uid.
    /// </summary>
    /// <remarks>
    /// Must be stable across processes: the #43 spike established that a supervisor's
    /// selective re-run and <c>[Isolated]</c> scheduling both depend on a uid meaning the same
    /// test in a later process. This is the same string <c>BobcatRunner</c> uses as the retry
    /// budget's test id, deliberately — one identity for one scenario, everywhere.
    /// </remarks>
    public static string Uid(string featureTitle, string scenarioTitle)
        => $"{featureTitle}/{scenarioTitle}";

    public static string Uid(FeatureDefinition feature, ScenarioDefinition scenario)
        => Uid(feature.Title, scenario.Title);

    /// <summary>
    /// What a Test Explorer shows. Qualified by feature because MTP nodes are a flat list here —
    /// a bare scenario title like "Empty invoice OK" is ambiguous across features.
    /// </summary>
    public static string DisplayName(string featureTitle, string scenarioTitle)
        => $"{featureTitle}: {scenarioTitle}";

    /// <summary>
    /// Gherkin tags as MTP metadata. This is the channel the #43 spike found survives every
    /// front-end intact, and it is how a supervisor reads <c>@isolated</c> / <c>@recycle(...)</c>
    /// off a Bobcat worker without knowing anything about Gherkin.
    /// </summary>
    public static IEnumerable<TestMetadataProperty> Traits(IEnumerable<string> tags)
    {
        foreach (var trait in ResilienceTags.ToTraits(tags))
        {
            yield return new TestMetadataProperty(trait.Key, trait.Value);
        }
    }

    /// <summary>
    /// The node state for a finished scenario.
    /// </summary>
    /// <remarks>
    /// The <c>failed</c>/<c>error</c> split is deliberate and matches what xUnit and tUnit put on
    /// the wire: an assertion disagreed versus an exception escaped. A supervisor's
    /// <see cref="Disposition"/> policy keys off exactly this distinction, so Bobcat must honour
    /// it rather than reporting everything as one kind of failure.
    /// </remarks>
    public static IProperty StateFor(ScenarioResult result)
    {
        var results = result.Results;

        if (results.Counts.Succeeded) return PassedTestNodeStateProperty.CachedInstance;

        var exception = results.AllExceptions().FirstOrDefault();

        // Errors mean something threw; wrongs mean a comparison disagreed.
        if (results.Counts.Errors > 0)
        {
            return new ErrorTestNodeStateProperty(exception ?? new SpecFailedException(Describe(results)));
        }

        return new FailedTestNodeStateProperty(exception ?? new SpecFailedException(Describe(results)));
    }

    /// <summary>
    /// The node state for a scenario the run planned and never executed — a resource that would
    /// not start, a failed preflight, a <c>BeforeAll</c> that threw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as <c>error</c>, and the alternatives were weighed. <c>skipped</c> is what the
    /// word suggests, but a supervisor counts a skipped test as succeeded (it is the framework's
    /// own "not applicable"), so a suite whose database never came up would read as green.
    /// Publishing nothing is what issue #123 started from: a run with no verdicts is exactly what
    /// a crashed worker looks like, and the supervisor rightly calls that <c>Indeterminate</c>.
    /// An <c>error</c> carrying the harness exception is the honest state: the scenario has no
    /// result because an exception stood in its way, the message says which one, and a
    /// supervisor sees a reported failure it can act on rather than silence it cannot.
    /// </para>
    /// <para>
    /// The distinction from a real crash is therefore structural — every planned node gets a
    /// verdict and the process exits normally — not a matter of which state was chosen.
    /// </para>
    /// </remarks>
    public static IProperty StateFor(NotRunScenario scenario)
        => new ErrorTestNodeStateProperty(
            new ScenarioNotRunException($"{scenario.Title} did not run: {scenario.Reason}", scenario.Cause),
            $"did not run: {scenario.Reason}");

    /// <summary>
    /// Metadata for a node that never ran. <c>bobcat.outcome</c> is the same key a finished
    /// scenario carries, with a value outside <see cref="RunOutcome"/> — there is no attempt to
    /// describe, and pretending otherwise would be the made-up result this whole path avoids.
    /// </summary>
    public static IEnumerable<TestMetadataProperty> OutcomeMetadata(NotRunScenario scenario)
    {
        yield return new TestMetadataProperty("bobcat.outcome", "NotRun");
        yield return new TestMetadataProperty("bobcat.notRunReason", scenario.Reason);
    }

    /// <summary>Wall time for the scenario's final attempt.</summary>
    public static TimingProperty TimingFor(ScenarioResult result)
    {
        var start = result.Results.StartTime;
        var end = result.Results.EndTime;
        if (end < start) end = start;

        return new TimingProperty(new TimingInfo(start, end, end - start));
    }

    /// <summary>
    /// Extra metadata a supervisor or a human wants after the fact. Retry history is included
    /// here rather than folded into the pass state, so a scenario that only passed on its third
    /// attempt cannot be read back as a clean pass.
    /// </summary>
    public static IEnumerable<TestMetadataProperty> OutcomeMetadata(ScenarioResult result)
    {
        yield return new TestMetadataProperty("bobcat.outcome", result.Outcome.ToString());

        if (result.WasRetried)
        {
            yield return new TestMetadataProperty("bobcat.attempts", result.AttemptCount.ToString());
        }

        foreach (var unsupported in result.UnsupportedDispositions)
        {
            yield return new TestMetadataProperty("bobcat.unsupportedDisposition", unsupported);
        }
    }

    /// <summary>
    /// A readable account of why a scenario failed, for hosts that only surface a message.
    /// Built from the structured cell results rather than a rendered display string.
    /// </summary>
    public static string Describe(ExecutionResults results)
    {
        var builder = new StringBuilder();

        foreach (var step in results.Steps)
        {
            // A step's own status can be 'success' while its cells disagree — comparison
            // results live on the cells, and the executor marks the step successful whenever it
            // completed without throwing. So the cells decide, not the step status.
            var cells = step.Cells
                .Where(c => c.Status is not (ResultStatus.success or ResultStatus.ok))
                .ToList();

            var stepFailed = step.StepStatus is not (ResultStatus.success or ResultStatus.ok);
            if (!stepFailed && cells.Count == 0 && step.Exception is null) continue;

            builder.Append(step.StepText ?? step.StepId);

            if (cells.Count > 0)
            {
                var detail = cells.Select(c => c.Expected is null && c.Actual is null
                    ? $"{c.Name}: {c.DisplayText}"
                    : $"{c.Name}: expected {c.Expected ?? "(none)"}, got {c.Actual ?? "(none)"}");

                builder.Append(" — ").Append(string.Join("; ", detail));
            }
            else if (step.Exception is not null)
            {
                builder.Append(" — ").Append(step.Exception.Message);
            }

            builder.AppendLine();
        }

        var message = builder.ToString().Trim();
        return message.Length > 0 ? message : "the scenario failed";
    }
}

/// <summary>
/// Stands in when a scenario failed on comparisons alone, so there is no real exception to
/// report. MTP's failed/error states both want one.
/// </summary>
public sealed class SpecFailedException(string message) : Exception(message);

/// <summary>
/// The exception an MTP node in error carries when its scenario never ran. The inner exception
/// is the harness failure itself — the resource's own exception, say — so a reader gets the
/// real stack, not one synthesized here.
/// </summary>
public sealed class ScenarioNotRunException(string message, Exception? cause) : Exception(message, cause);
