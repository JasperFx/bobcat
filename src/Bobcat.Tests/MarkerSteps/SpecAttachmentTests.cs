using System.Text;
using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Issue #571: failure evidence — <see cref="SpecAttachment"/> and <see cref="IFailureCapture"/>.
/// In the marker-step-run collection because the results collector, the verbose switch and the
/// capture registry are all process-wide.
/// </summary>
[Collection("marker-step-run")]
public class SpecAttachmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bobcat-attachment-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _sink = new();
    private readonly List<(string Name, string MediaType, byte[] Bytes)> _platform = new();
    private readonly string _feature = "Attachments " + Guid.NewGuid().ToString("N")[..6];

    public SpecAttachmentTests()
    {
        ScenarioReportVisibility.Verbose = false;
    }

    public void Dispose()
    {
        SpecAttachment.Reset();
        ScenarioReportVisibility.Reset();
        ResultsFile.Reset();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ScenarioRecorder.Recording begin(string scenario = "a scenario")
        => ScenarioRecorder.Begin(_feature, scenario, _sink, Guid.NewGuid());

    private static void failCell(string name = "count")
        => ScenarioRecorder.RecordCell(new CellResult(name, ResultStatus.failed) { Expected = "1", Actual = "2" });

    private ScenarioFinished finished() => _sink.Events.OfType<ScenarioFinished>().Single();

    private IDisposable platform()
        => SpecAttachment.OpenPlatformSink((name, type, bytes) => _platform.Add((name, type, bytes)));

    [Fact]
    public void an_attachment_hangs_on_the_step_in_progress_or_on_the_scenario_between_steps()
    {
        using var _ = platform();
        var recording = begin();

        using (ScenarioRecorder.Step("Then", "the page shows the total"))
        {
            SpecAttachment.Add("page.png", [1, 2, 3], "image/png");
            failCell();
        }

        SpecAttachment.AddText("log.txt", "between steps");
        recording.Dispose();

        var render = SpecRender.FromRecording(recording);
        var step = render.Steps.Single();
        step.Attachments.Single().Name.ShouldBe("page.png");
        step.Attachments.Single().MediaType.ShouldBe("image/png");
        step.Attachments.Single().Size.ShouldBe(3);
        render.Attachments.Single().Name.ShouldBe("log.txt");

        // No results directory: the test platform's own attachments, numbered so names never collide.
        _platform.Select(x => x.Name).ShouldBe(["01-page.png", "02-log.txt"]);
        _platform[0].Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        Encoding.UTF8.GetString(_platform[1].Bytes).ShouldBe("between steps");

        // The wire carries the reference, with the step it hangs on, and no path (none was written).
        var wire = finished().Attachments.ShouldNotBeNull();
        wire.Select(x => (x.Name, x.StepId, x.Size, x.Path)).ShouldBe(
            [("page.png", "s1", 3L, null), ("log.txt", null, 13L, null)]);
    }

    [Fact]
    public void a_passing_scenario_keeps_none_because_attachments_are_latched_like_reports()
    {
        using var _ = platform();
        var recording = begin();
        using (ScenarioRecorder.Step("Then", "it works")) SpecAttachment.Add("page.png", [1], "image/png");
        recording.Dispose();

        recording.Attachments.ShouldBeEmpty();
        recording.Steps.Single().Attachments.ShouldBeEmpty();
        _platform.ShouldBeEmpty();
        finished().Attachments.ShouldBeNull();
    }

    [Fact]
    public void a_verbose_run_keeps_a_passing_scenarios_attachments()
    {
        ScenarioReportVisibility.Verbose = true;
        using var _ = platform();
        var recording = begin();
        using (ScenarioRecorder.Step("Then", "it works")) SpecAttachment.Add("page.png", [1], "image/png");
        recording.Dispose();

        _platform.Single().Name.ShouldBe("01-page.png");
        finished().Attachments.ShouldNotBeNull().Single().Name.ShouldBe("page.png");
    }

    [Fact]
    public void with_a_results_directory_attachments_are_files_beside_the_results_and_the_results_name_them()
    {
        using var _ = platform();
        var info = new MonitorRunInfo(Guid.NewGuid(), "My.Specs", "/repo", "main", "xunit");
        ResultsFile.BeginRun(info, _directory);

        var recording = begin("shows the total");
        using (ScenarioRecorder.Step("Then", "the page shows the total"))
        {
            SpecAttachment.Add("page.png", [9, 9], "image/png");
            failCell();
        }

        recording.Dispose();
        var resultsPath = ResultsFile.EndRun().ShouldNotBeNull();

        // Not to the platform as well: one home per run.
        _platform.ShouldBeEmpty();

        var relative = $"attachments/{info.RunId:N}/{_feature.Replace(' ', '_')}_shows_the_total/01-page.png";
        File.ReadAllBytes(Path.Combine(_directory, relative)).ShouldBe(new byte[] { 9, 9 });

        var json = JsonDocument.Parse(File.ReadAllText(resultsPath)).RootElement;
        var attachment = json.GetProperty("scenarios")[0].GetProperty("steps")[0].GetProperty("attachments")[0];
        attachment.GetProperty("name").GetString().ShouldBe("page.png");
        attachment.GetProperty("mediaType").GetString().ShouldBe("image/png");
        attachment.GetProperty("size").GetInt64().ShouldBe(2);
        attachment.GetProperty("path").GetString().ShouldBe(relative);

        // The wire gets the absolute path a local console can open, and never the bytes.
        finished().Attachments.ShouldNotBeNull().Single().Path
            .ShouldBe(Path.GetFullPath(Path.Combine(_directory, relative)));
    }

    [Fact]
    public void an_attachment_over_the_size_cap_is_a_reference_saying_so_never_a_truncation()
    {
        using var _ = platform();
        SpecAttachment.MaxBytes = 4;
        var recording = begin();
        using (ScenarioRecorder.Step("Then", "it fails"))
        {
            SpecAttachment.Add("dump.bin", new byte[10]);
            failCell();
        }

        recording.Dispose();

        _platform.ShouldBeEmpty();
        var wire = finished().Attachments.ShouldNotBeNull().Single();
        wire.Size.ShouldBe(10);
        wire.Omitted.ShouldBe("omitted: 10 B is over the 4 B cap");
    }

    [Fact]
    public void attachments_past_the_per_scenario_cap_are_counted_and_rendered_as_and_n_more()
    {
        using var _ = platform();
        SpecAttachment.MaxPerScenario = 2;
        var recording = begin();
        for (var i = 0; i < 5; i++) SpecAttachment.Add($"shot{i}.png", [1], "image/png");
        using (ScenarioRecorder.Step("Then", "it fails")) failCell();
        recording.Dispose();

        recording.Attachments.Count.ShouldBe(2);
        recording.SuppressedAttachments.ShouldBe(3);
        finished().SuppressedAttachments.ShouldBe(3);
        PlainSpecText.Lines(SpecRender.FromRecording(recording))
            .ShouldContain(x => x.Contains("…and 3 more attachments not kept"));
    }

    [Fact]
    public void a_file_is_read_when_it_is_added_and_its_media_type_comes_from_its_extension()
    {
        using var _ = platform();
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "screen.png");
        File.WriteAllBytes(path, [7, 7, 7]);

        var recording = begin();
        using (ScenarioRecorder.Step("Then", "it fails"))
        {
            SpecAttachment.AddFile(path);
            failCell();
        }

        File.Delete(path); // a screenshot tool's temp file may be gone by the time the scenario closes
        recording.Dispose();

        _platform.Single().ShouldSatisfyAllConditions(
            x => x.Name.ShouldBe("01-screen.png"),
            x => x.MediaType.ShouldBe("image/png"),
            x => x.Bytes.ShouldBe(new byte[] { 7, 7, 7 }));
    }

    [Fact]
    public void adding_outside_a_recording_does_nothing()
    {
        Should.NotThrow(() => SpecAttachment.Add("x.png", [1]));
        Should.NotThrow(() => SpecAttachment.AddFile("/no/such/file.png"));
    }

    [Fact]
    public void a_failure_capture_runs_for_the_innermost_failing_step_and_its_attachment_hangs_there()
    {
        using var _ = platform();
        var capture = new Capture(_feature);
        SpecAttachment.RegisterFailureCapture(capture);
        SpecAttachment.RegisterFailureCapture(capture); // once is once

        var recording = begin();
        using (ScenarioRecorder.Step("Given", "an open account")) { }
        using (ScenarioRecorder.Step("Then", "the statement is right"))
        {
            using (ScenarioRecorder.Step("Then", "the balance is 10")) failCell("balance");
        }

        recording.Dispose();

        // Once, for the child: the parent is red only because the child was.
        capture.Calls.Select(x => x.Text).ShouldBe(["And the balance is 10"]);
        capture.Calls.Single().Status.ShouldBe(ResultStatus.failed);
        capture.Calls.Single().Uid.ShouldBe($"{_feature}/a scenario");

        var inner = recording.Steps.Single(x => x.Text == "the balance is 10");
        inner.Attachments.Single().Name.ShouldBe("screenshot.png");
        finished().Attachments.ShouldNotBeNull().Single().StepId.ShouldBe(inner.StepId);
    }

    [Fact]
    public void a_failure_capture_that_throws_or_overruns_is_stepped_over_and_never_fails_the_test()
    {
        using var _ = platform();
        SpecAttachment.CaptureTimeout = TimeSpan.FromMilliseconds(200);
        SpecAttachment.RegisterFailureCapture(new Throwing(_feature));
        SpecAttachment.RegisterFailureCapture(new Hanging(_feature));
        var working = new Capture(_feature);
        SpecAttachment.RegisterFailureCapture(working);

        var lines = new List<string>();
        using var output = SpecOutput.Open(lines.Add);

        var recording = begin();
        using (ScenarioRecorder.Step("When", "the page is opened"))
        {
            ScenarioRecorder.CurrentStep!.Failure = new InvalidOperationException("boom");
        }

        Should.NotThrow(() => recording.Dispose());

        // The step's own verdict and failure are untouched; the working capture still got its turn.
        recording.Steps.Single().Status.ShouldBe(ResultStatus.error);
        recording.Steps.Single().Failure!.Message.ShouldBe("boom");
        working.Calls.Single().Failure!.Message.ShouldBe("boom");
        _platform.Single().Name.ShouldBe("01-screenshot.png");

        lines.ShouldContain(x => x.Contains("failure capture Throwing threw") && x.Contains("no browser"));
        lines.ShouldContain(x => x.Contains("failure capture Hanging took longer than"));
    }

    [Fact]
    public void a_passing_step_calls_no_capture()
    {
        var capture = new Capture(_feature);
        SpecAttachment.RegisterFailureCapture(capture);

        var recording = begin();
        using (ScenarioRecorder.Step("Then", "it works")) { }
        recording.Dispose();

        capture.Calls.ShouldBeEmpty();
    }

    private sealed class Capture(string feature) : IFailureCapture
    {
        public List<FailedStep> Calls { get; } = new();

        public Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
        {
            // Other classes' failing steps may run while this one is registered; only ours count.
            if (step.Feature != feature) return Task.FromResult<IReadOnlyList<CapturedAttachment>>([]);

            lock (Calls) Calls.Add(step);
            return Task.FromResult<IReadOnlyList<CapturedAttachment>>(
                [new CapturedAttachment("screenshot.png", [4, 2], "image/png")]);
        }
    }

    private sealed class Throwing(string feature) : IFailureCapture
    {
        public Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
            => step.Feature == feature
                ? throw new InvalidOperationException("no browser")
                : Task.FromResult<IReadOnlyList<CapturedAttachment>>([]);
    }

    private sealed class Hanging(string feature) : IFailureCapture
    {
        public async Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation)
        {
            if (step.Feature == feature) await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return [];
        }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToArray(); }
        }
    }
}
