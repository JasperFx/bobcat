using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bobcat.Ui.Contracts;

namespace Bobcat.Ui.Runs;

/// <summary>
/// Projects a run onto Cucumber Messages (bobcat#848): the NDJSON envelope stream every Cucumber-family
/// reporter reads — the Cucumber HTML formatter, Allure, Serenity-style requirement coverage and the
/// living-documentation tools Reqnroll users rely on. One envelope per line, each with exactly one
/// field set, in the order a Cucumber run would emit them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything upstream of the verdicts is synthesised.</b> A Bobcat specification is an ordinary test,
/// not a <c>.feature</c> file, so the <c>source</c>, <c>gherkinDocument</c> and <c>pickle</c> envelopes are
/// written from what the run rendered: one feature per <c>[BobcatFeature]</c> (the identity's feature
/// half), one scenario per specification, and the steps from the rendered step text of the final
/// attempt. The Gherkin text is built line by line so every <c>location.line</c> points at the line it
/// describes. <c>testCase*</c> / <c>testStep*</c> carry the recorded verdicts and durations, cells and
/// step logs ride as <c>attachment</c>s on their step, a scenario's reports (bobcat#408) as attachments
/// on the test case, and <c>meta.ci.git</c> carries the run's <c>BOBCAT_RUN_TAG</c> so the correlation
/// Bobcat already does survives into the reporters.
/// </para>
/// <para>
/// <b>A scenario that recorded no steps gets one.</b> A supervised foreign test (issue #195) and a spec
/// whose publisher sent no steps still have a verdict, and Cucumber derives a test case's status from
/// its steps: with none, every reporter shows nothing. So such a scenario gets one <c>*</c> step named
/// for itself, carrying the verdict and the error message. It is the one place the export says something
/// the run did not say in those words, and the keyword <c>*</c> marks it as such.
/// </para>
/// <para>
/// <b>Deterministic.</b> Ids are positional (<c>feature-1</c>, <c>scenario-3</c>, <c>step-3-2</c>) and
/// timestamps come from the recorded start and durations, so exporting the same run twice gives the same
/// bytes. Unmeasured is never zero-filled where the schema allows it: a scenario with no start takes
/// the run's, and a run with none the epoch, which is the one place the schema's <c>required</c>
/// forces a figure.
/// </para>
/// </remarks>
public static class CucumberMessagesExport
{
    /// <summary>What the export answers with over HTTP: newline-delimited JSON, one envelope per line.</summary>
    public const string MediaType = "application/x-ndjson";

    /// <summary>The Cucumber Messages release whose schema this export is validated against (bobcat#848).</summary>
    public const string ProtocolVersion = "35.0.0";

    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        // The schema's objects admit no null-valued properties where a field is optional, so an unset
        // optional field is left out rather than written as null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(RunProjection run)
    {
        var writer = new Envelopes();
        var runStartedId = $"run-{run.RunId:N}";
        var runStart = run.StartedAt ?? DateTimeOffset.UnixEpoch;

        writer.Add(new { meta = meta(run) });

        var features = run.Scenarios
            .OrderBy(s => s.Feature, StringComparer.Ordinal).ThenBy(s => s.Scenario, StringComparer.Ordinal)
            .GroupBy(s => s.Feature)
            .Select((group, index) => new FeatureDocument(group.Key, group.ToList(), index + 1))
            .ToList();

        foreach (var feature in features)
        {
            writer.Add(new { source = new { uri = feature.Uri, data = feature.Gherkin, mediaType = "text/x.cucumber.gherkin+plain" } });
            writer.Add(new { gherkinDocument = feature.Document });
            foreach (var scenario in feature.Scenarios) writer.Add(new { pickle = scenario.Pickle });
        }

        writer.Add(new { testRunStarted = new { id = runStartedId, timestamp = timestamp(runStart) } });

        foreach (var scenario in features.SelectMany(f => f.Scenarios))
        {
            writer.Add(new
            {
                testCase = new
                {
                    id = scenario.TestCaseId,
                    pickleId = scenario.PickleId,
                    testSteps = scenario.Steps.Select(s => new { id = s.TestStepId, pickleStepId = s.PickleStepId }).ToArray(),
                    testRunStartedId = runStartedId,
                }
            });

            var attempt = 0;
            foreach (var prior in scenario.Projection.PriorAttempts.OrderBy(a => a.Attempt))
            {
                renderAttempt(writer, scenario, attempt++, prior.Steps, prior.ErrorMessage, outcome: "Failed",
                    startedAt: scenario.Projection.StartedAt ?? runStart, willBeRetried: true, reports: []);
            }

            renderAttempt(writer, scenario, attempt, scenario.Projection.Steps, scenario.Projection.ErrorMessage,
                scenario.Projection.Outcome, scenario.Projection.StartedAt ?? runStart, willBeRetried: false,
                scenario.Projection.Reports);
        }

        var finishedAt = run.FinishedAt ?? features.SelectMany(f => f.Scenarios)
            .Select(s => s.Projection.FinishedAt).Where(x => x is not null).DefaultIfEmpty(runStart).Max()!.Value;
        writer.Add(new
        {
            testRunFinished = new
            {
                success = run.Finished && run.Scenarios.All(s => s.Outcome is "CleanPass" or "PassOnRetry" or "AcceptanceFailing"),
                timestamp = timestamp(finishedAt),
                testRunStartedId = runStartedId,
                message = run.Finished ? null : "the run has not finished: exported mid-run, or the publisher died",
            }
        });

        return writer.ToString();
    }

    private static void renderAttempt(Envelopes writer, ScenarioDocument scenario, int attempt, IReadOnlyList<StepProjection> recorded,
        string? errorMessage, string? outcome, DateTimeOffset startedAt, bool willBeRetried, IReadOnlyList<ScenarioReportInfo> reports)
    {
        var testCaseStartedId = $"{scenario.TestCaseId}-attempt-{attempt}";
        writer.Add(new { testCaseStarted = new { id = testCaseStartedId, attempt, testCaseId = scenario.TestCaseId, timestamp = timestamp(startedAt) } });

        var clock = startedAt;
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            // An earlier attempt's steps are matched by position: it ran the same body, perhaps not all of it
            var ran = i < recorded.Count ? recorded[i] : null;
            if (step.Synthetic) ran = null;

            writer.Add(new { testStepStarted = new { testCaseStartedId, testStepId = step.TestStepId, timestamp = timestamp(clock) } });

            var durationMs = step.Synthetic ? scenario.Projection.DurationMs ?? 0 : ran?.DurationMs ?? 0;
            var status = step.Synthetic ? statusOf(outcome) : stepStatus(ran);
            var message = step.Synthetic ? errorMessage : ran?.ErrorMessage;
            var exception = ran?.Exception;

            foreach (var attachment in attachmentsOf(ran, testCaseStartedId, step.TestStepId)) writer.Add(new { attachment });

            clock = clock.AddMilliseconds(durationMs);
            writer.Add(new
            {
                testStepFinished = new
                {
                    testCaseStartedId,
                    testStepId = step.TestStepId,
                    testStepResult = new
                    {
                        status,
                        duration = duration(durationMs),
                        message = status is "FAILED" or "UNDEFINED" ? message : null,
                        exception = exception is null ? null : new { type = exception.Type ?? "Exception", message = exception.Message, stackTrace = exception.StackTrace },
                    },
                    timestamp = timestamp(clock),
                }
            });
        }

        foreach (var report in reports)
        {
            writer.Add(new
            {
                attachment = new
                {
                    testCaseStartedId,
                    mediaType = "application/json",
                    contentEncoding = "IDENTITY",
                    fileName = $"{sanitize(report.ShortTitle ?? report.Title)}.json",
                    body = JsonSerializer.Serialize(new
                    {
                        title = report.Title,
                        columns = report.Columns,
                        cells = report.Cells.Select(cell).ToArray(),
                        suppressedRows = report.SuppressedRows,
                    }, serializerOptions),
                }
            });
        }

        var finished = scenario.Projection.FinishedAt is { } at && !willBeRetried ? at : clock;
        writer.Add(new { testCaseFinished = new { testCaseStartedId, timestamp = timestamp(finished), willBeRetried } });
    }

    /// <summary>A step's cells as one JSON attachment and its log lines as one text attachment, each on the step.</summary>
    private static IEnumerable<object> attachmentsOf(StepProjection? step, string testCaseStartedId, string testStepId)
    {
        if (step is null) yield break;

        if (step.Cells.Count > 0)
        {
            yield return new
            {
                testCaseStartedId,
                testStepId,
                mediaType = "application/json",
                contentEncoding = "IDENTITY",
                fileName = "cells.json",
                body = JsonSerializer.Serialize(new { columns = step.Columns, cells = step.Cells.Select(cell).ToArray() }, serializerOptions),
            };
        }

        if (step.Logs.Count > 0)
        {
            yield return new
            {
                testCaseStartedId,
                testStepId,
                mediaType = "text/plain",
                contentEncoding = "IDENTITY",
                fileName = "log.txt",
                body = string.Join("\n", step.Logs),
            };
        }
    }

    /// <summary>A cell as the attachment writes it: what it compared, judged or not (issue #396's one-thing rule kept).</summary>
    private static object cell(StepCell cell) => new
    {
        name = cell.Name,
        status = cell.Status,
        expected = cell.Expected,
        actual = cell.Actual,
        value = cell.Value,
        note = cell.Note,
        comparison = cell.Comparison,
        rowIndex = cell.RowIndex < 0 ? (int?)null : cell.RowIndex,
    };

    private static object meta(RunProjection run)
    {
        var version = typeof(CucumberMessagesExport).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The run's correlation: the tag (BOBCAT_RUN_TAG), the branch and the repository the publisher
        // reported. Git.revision is required by the schema and the publisher reports no commit sha of
        // its own (a detached HEAD arrives as the short sha in Branch), so revision carries the branch
        // field as reported, never an invented one. Omitted whole when the run reported none of it.
        object? ci = run.Tag is null && run.Branch is null && run.RepositorySlug is null
            ? null
            : new
            {
                name = "bobcat",
                buildNumber = run.RunId.ToString(),
                git = new
                {
                    remote = run.RepositorySlug ?? run.Repository,
                    revision = run.Branch ?? "",
                    branch = run.Branch,
                    tag = run.Tag,
                },
            };

        return new
        {
            protocolVersion = ProtocolVersion,
            implementation = new { name = "bobcat", version },
            runtime = new { name = ".NET", version = Environment.Version.ToString() },
            os = new { name = osName() },
            cpu = new { name = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() },
            ci,
        };
    }

    private static string osName()
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : RuntimeInformation.OSDescription;

    /// <summary>A recorded step's verdict in Cucumber's words; a step still running when exported is UNKNOWN.</summary>
    private static string stepStatus(StepProjection? step) => step switch
    {
        null => "SKIPPED",
        { Status: StepProjection.Running } => "UNKNOWN",
        _ when StepVerdict.IsPassed(step.Status) => "PASSED",
        // A spec defect (a column naming nothing, a cell that cannot be read) is UNDEFINED: the step
        // could not be judged, which is Cucumber's word for a step with nothing to run it
        { Status: var s } when s.Equals("invalid", StringComparison.OrdinalIgnoreCase) => "UNDEFINED",
        { Status: var s } when s.Equals("skipped", StringComparison.OrdinalIgnoreCase) => "SKIPPED",
        _ => "FAILED",
    };

    /// <summary>A scenario's RunOutcome as a step status, for the one synthetic step a step-less scenario gets.</summary>
    private static string statusOf(string? outcome) => outcome switch
    {
        "CleanPass" or "PassOnRetry" => "PASSED",
        "Failed" or "Aborted" => "FAILED",
        // bobcat#545: a failing Acceptance spec is what the test platform reported it, a skip
        "AcceptanceFailing" => "SKIPPED",
        _ => "UNKNOWN",
    };

    private static object timestamp(DateTimeOffset at)
    {
        var ticks = at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        return new { seconds = ticks / TimeSpan.TicksPerSecond, nanos = (int)(ticks % TimeSpan.TicksPerSecond * 100) };
    }

    private static object duration(long milliseconds)
        => new { seconds = milliseconds / 1000, nanos = (int)(milliseconds % 1000 * 1_000_000) };

    private static string sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        return builder.Length == 0 ? "report" : builder.ToString();
    }

    /// <summary>One feature: its synthesised Gherkin text with the document and pickles that point into it.</summary>
    private sealed class FeatureDocument
    {
        public string Uri { get; }
        public string Gherkin { get; }
        public object Document { get; }
        public IReadOnlyList<ScenarioDocument> Scenarios { get; }

        public FeatureDocument(string feature, IReadOnlyList<ScenarioProjection> scenarios, int index)
        {
            var name = feature.Length > 0 ? feature : "(no feature)";
            Uri = $"bobcat://{sanitize(name)}.feature";

            var text = new StringBuilder();
            var line = 0;
            var children = new List<object>();
            var documents = new List<ScenarioDocument>();

            text.Append("Feature: ").Append(name).Append('\n');
            line++;
            var featureLine = line;

            var scenarioIndex = 0;
            foreach (var projection in scenarios)
            {
                scenarioIndex++;
                text.Append('\n');
                line++;
                var scenarioId = $"scenario-{index}-{scenarioIndex}";
                text.Append("  Scenario: ").Append(projection.Scenario).Append('\n');
                line++;
                var scenarioLine = line;

                var steps = new List<StepDocument>();
                var recorded = projection.Steps;
                if (recorded.Count == 0)
                {
                    text.Append("    * ").Append(projection.Scenario).Append('\n');
                    line++;
                    steps.Add(new StepDocument($"{scenarioId}-step-1", "* ", "Unknown", projection.Scenario, line, Synthetic: true));
                }

                for (var i = 0; i < recorded.Count; i++)
                {
                    var (keyword, keywordType) = keywordOf(recorded[i].Kind);
                    text.Append("    ").Append(keyword).Append(recorded[i].Text).Append('\n');
                    line++;
                    steps.Add(new StepDocument($"{scenarioId}-step-{i + 1}", keyword, keywordType, recorded[i].Text, line, Synthetic: false));
                }

                children.Add(new
                {
                    scenario = new
                    {
                        location = new { line = scenarioLine, column = 3 },
                        tags = Array.Empty<object>(),
                        keyword = "Scenario",
                        name = projection.Scenario,
                        description = "",
                        steps = steps.Select(s => new
                        {
                            location = new { line = s.Line, column = 5 },
                            keyword = s.Keyword,
                            keywordType = s.KeywordType,
                            text = s.Text,
                            id = s.Id,
                        }).ToArray(),
                        examples = Array.Empty<object>(),
                        id = scenarioId,
                    }
                });

                documents.Add(new ScenarioDocument(projection, scenarioId, Uri, steps));
            }

            Gherkin = text.ToString();
            Document = new
            {
                uri = Uri,
                feature = new
                {
                    location = new { line = featureLine, column = 1 },
                    tags = Array.Empty<object>(),
                    language = "en",
                    keyword = "Feature",
                    name,
                    description = "",
                    children,
                },
                comments = Array.Empty<object>(),
            };
            Scenarios = documents;
        }

        private static (string Keyword, string KeywordType) keywordOf(string kind) => kind switch
        {
            "Given" => ("Given ", "Context"),
            "When" => ("When ", "Action"),
            "Then" => ("Then ", "Outcome"),
            "And" => ("And ", "Conjunction"),
            "But" => ("But ", "Conjunction"),
            _ => ("* ", "Unknown"),
        };
    }

    private sealed record StepDocument(string Id, string Keyword, string KeywordType, string Text, int Line, bool Synthetic)
    {
        public string PickleStepId => $"{Id}-pickle";
        public string TestStepId => $"{Id}-test";
    }

    private sealed class ScenarioDocument(ScenarioProjection projection, string scenarioId, string uri, IReadOnlyList<StepDocument> steps)
    {
        public ScenarioProjection Projection => projection;
        public string PickleId => $"{scenarioId}-pickle";
        public string TestCaseId => $"{scenarioId}-test";
        public IReadOnlyList<StepDocument> Steps => steps;

        public object Pickle => new
        {
            id = PickleId,
            uri,
            name = projection.Scenario,
            language = "en",
            steps = steps.Select(s => new
            {
                id = s.PickleStepId,
                text = s.Text,
                type = s.KeywordType == "Conjunction" ? "Unknown" : s.KeywordType,
                astNodeIds = new[] { s.Id },
            }).ToArray(),
            tags = Array.Empty<object>(),
            astNodeIds = new[] { scenarioId },
        };
    }

    private sealed class Envelopes
    {
        private readonly StringBuilder _lines = new();

        public void Add(object envelope) => _lines.Append(JsonSerializer.Serialize(envelope, serializerOptions)).Append('\n');

        public override string ToString() => _lines.ToString();
    }
}
