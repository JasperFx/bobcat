using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// Failure evidence for a specification: a screenshot, a page's HTML, a dump — bytes a reader of
/// the failure wants beside the step that went red (issue #571).
/// </summary>
/// <remarks>
/// <para>
/// <b>Onto the step in progress, or the scenario.</b> An attachment added while a step helper runs
/// hangs on that step; one added between steps hangs on the scenario. Outside a recording it is a
/// no-op, like every other ambient Bobcat call.
/// </para>
/// <para>
/// <b>Latched like reports.</b> Attachments are kept only for a scenario that failed, unless the
/// run is verbose (<c>--verbose</c>, <c>BOBCAT_VERBOSE_REPORTS</c>) — the same rule
/// <see cref="ScenarioReportVisibility"/> applies, so a green suite pays nothing to write them.
/// </para>
/// <para>
/// <b>Where they go.</b> With a results directory (<c>BOBCAT_RESULTS</c>, <c>bobcat run
/// --results</c>) each is a file under <c>attachments/</c> beside the results file, which names it
/// on its step. Otherwise they go to the test platform's own attachments (xUnit v3's
/// <c>TestContext.AddAttachment</c>, a TUnit artifact), so TRX and the IDE show them. The monitor
/// wire carries a reference only — name, media type, size and path — never the bytes.
/// </para>
/// <para>
/// <b>Capped.</b> An attachment over <see cref="MaxBytes"/> is kept as a reference that says it was
/// omitted, never truncated (half a PNG is not evidence); past <see cref="MaxPerScenario"/> the rest
/// are counted and rendered as "…and N more". The 698MB lesson from
/// <c>docs/tutorials/agent-friendly-tests.md</c> again: saying too much must not fault a run.
/// </para>
/// </remarks>
public static class SpecAttachment
{
    /// <summary>The default per-attachment size cap: 10 MB.</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024;

    /// <summary>The default number of attachments one scenario keeps.</summary>
    public const int DefaultMaxPerScenario = 20;

    /// <summary>Attachments larger than this are kept as a reference only, marked omitted.</summary>
    public static long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>Attachments one scenario keeps; the rest are counted, not kept.</summary>
    public static int MaxPerScenario { get; set; } = DefaultMaxPerScenario;

    /// <summary>
    /// How long one <see cref="IFailureCapture"/> may take for one failed step before it is stepped
    /// over. Ten seconds by default.
    /// </summary>
    public static TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Attach <paramref name="content"/> to the step in progress, or to the scenario between steps.
    /// </summary>
    public static void Add(string name, byte[] content, string mediaType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);

        var recording = ScenarioRecorder.Current;
        if (recording is null) return;

        recording.Attach(recording.OpenStep, name, mediaType, content);
    }

    /// <summary>Attach text, as UTF-8.</summary>
    public static void AddText(string name, string text, string mediaType = "text/plain")
        => Add(name, System.Text.Encoding.UTF8.GetBytes(text ?? ""), mediaType);

    /// <summary>
    /// Attach a file, read now — a screenshot tool's temp file may be gone by the time the scenario
    /// closes. The name defaults to the file's, the media type to one guessed from its extension.
    /// </summary>
    public static void AddFile(string path, string? name = null, string? mediaType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var recording = ScenarioRecorder.Current;
        if (recording is null) return;

        name ??= Path.GetFileName(path);
        mediaType ??= MediaTypes.FromFileName(path);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            recording.AttachOmitted(recording.OpenStep, name, mediaType, 0, $"file not found: {path}");
            return;
        }

        // Checked before reading, so an oversized file is never pulled into memory.
        if (info.Length > MaxBytes)
        {
            recording.AttachOmitted(recording.OpenStep, name, mediaType, info.Length, OmittedForSize(info.Length));
            return;
        }

        recording.Attach(recording.OpenStep, name, mediaType, File.ReadAllBytes(path));
    }

    internal static string OmittedForSize(long size)
        => $"omitted: {Describe(size)} is over the {Describe(MaxBytes)} cap";

    /// <summary>A size as a reader wants it: bytes, KB or MB.</summary>
    public static string Describe(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB"
    };

    private static readonly object _gate = new();
    private static IFailureCapture[] _captures = [];

    /// <summary>
    /// Register a capture called for every step that goes failed or error. Registered once,
    /// explicitly — a Playwright base class's static constructor, a module initializer, an assembly
    /// fixture. Registering the same instance twice is one registration.
    /// </summary>
    public static void RegisterFailureCapture(IFailureCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);

        lock (_gate)
        {
            if (_captures.Contains(capture)) return;
            _captures = [.. _captures, capture];
        }
    }

    /// <summary>Remove a registered capture.</summary>
    public static void UnregisterFailureCapture(IFailureCapture capture)
    {
        lock (_gate) _captures = _captures.Where(x => !ReferenceEquals(x, capture)).ToArray();
    }

    internal static IReadOnlyList<IFailureCapture> FailureCaptures => _captures;

    /// <summary>
    /// Run every registered capture for a step that just failed, adding what each returns to that
    /// step. A capture that throws or runs past <see cref="CaptureTimeout"/> is logged and stepped
    /// over: evidence-gathering never fails a test.
    /// </summary>
    internal static void CaptureFailure(ScenarioRecorder.Recording recording, ScenarioRecorder.RecordedStep step)
    {
        var captures = _captures;
        if (captures.Length == 0) return;

        var context = new FailedStep(recording.Feature, recording.Scenario, step.ToString(), step.Status, step.Failure);
        var timeout = CaptureTimeout;

        foreach (var capture in captures)
        {
            using var cancellation = new CancellationTokenSource(timeout);

            try
            {
                // Task.Run: the capture runs on the pool with no synchronization context, so waiting
                // on it here cannot deadlock a runner that installs one. The execution context still
                // flows, so a capture finds the test's own AsyncLocal state (its browser page).
                var task = Task.Run(() => capture.CaptureAsync(context, cancellation.Token));

                if (!task.Wait(timeout))
                {
                    cancellation.Cancel();
                    Log($"Bobcat: failure capture {capture.GetType().Name} took longer than {timeout.TotalSeconds:0.#}s on '{step}' and was skipped.");
                    continue;
                }

                foreach (var attachment in task.Result ?? [])
                {
                    if (attachment is null) continue;
                    recording.Attach(step, attachment.Name, attachment.MediaType, attachment.Content);
                }
            }
            catch (Exception e)
            {
                var inner = e is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerException! : e;
                Log($"Bobcat: failure capture {capture.GetType().Name} threw on '{step}' and was skipped: {inner.GetType().Name}: {inner.Message}");
            }
        }
    }

    internal static void Log(string line)
    {
        if (SpecOutput.IsOpen)
        {
            SpecOutput.Write(line);
            return;
        }

        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // Nowhere to say it is not a reason to fail a test.
        }
    }

    private static readonly AsyncLocal<Action<string, string, byte[]>?> _platform = new();

    /// <summary>
    /// Hand this test's kept attachments to the test platform — <c>(name, mediaType, bytes)</c> —
    /// until the returned scope is disposed. Called by a runner adapter around one test, the same
    /// seam shape as <see cref="SpecOutput.Open"/>. Used only when no results directory was asked
    /// for.
    /// </summary>
    public static IDisposable OpenPlatformSink(Action<string, string, byte[]> sink)
    {
        var previous = _platform.Value;
        _platform.Value = sink;
        return new Scope(previous);
    }

    /// <summary>
    /// The same seam for a platform that attaches <i>files</i> — TUnit's artifacts, NUnit's
    /// <c>AddTestAttachment</c>, MSTest's <c>AddResultFile</c>. Each attachment is written to a fresh
    /// directory under the system temp directory and <paramref name="attachFile"/> is handed its path;
    /// the platform copies or references it from there.
    /// </summary>
    public static IDisposable OpenPlatformFileSink(Action<string> attachFile)
        => OpenPlatformSink((name, _, bytes) =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "bobcat-attachments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, Rendering.ResultsFile.Safe(name));
            File.WriteAllBytes(path, bytes);
            attachFile(path);
        });

    internal static Action<string, string, byte[]>? PlatformSink => _platform.Value;

    /// <summary>Test seam: forget every registration and setting.</summary>
    internal static void Reset()
    {
        lock (_gate) _captures = [];
        MaxBytes = DefaultMaxBytes;
        MaxPerScenario = DefaultMaxPerScenario;
        CaptureTimeout = TimeSpan.FromSeconds(10);
    }

    private sealed class Scope(Action<string, string, byte[]>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _platform.Value = previous;
        }
    }
}

/// <summary>
/// Evidence gathered automatically when a step fails — a screenshot of the browser page, the last
/// response body — registered once with <see cref="SpecAttachment.RegisterFailureCapture"/>
/// (issue #571).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a general step hook.</b> It is called only for a step that went failed or
/// error, and all it can do is return attachments. A <c>BeforeStep</c>/<c>AfterStep</c> hook invites
/// behaviour hidden around steps, which is what makes a Cucumber suite hard to read; this seam can
/// only produce evidence.
/// </para>
/// <para>
/// <b>It can never fail a test.</b> A capture that throws, or that runs past
/// <see cref="SpecAttachment.CaptureTimeout"/>, is logged to the test's output and stepped over.
/// It runs once per failing step — the innermost one, not every parent the failure bubbles up
/// through — on the thread pool, with the test's async-local state flowing in.
/// </para>
/// </remarks>
public interface IFailureCapture
{
    Task<IReadOnlyList<CapturedAttachment>> CaptureAsync(FailedStep step, CancellationToken cancellation);
}

/// <summary>An attachment an <see cref="IFailureCapture"/> returns.</summary>
public sealed record CapturedAttachment(string Name, byte[] Content, string MediaType = "application/octet-stream");

/// <summary>The step an <see cref="IFailureCapture"/> is called for.</summary>
/// <param name="Text">The step as it renders: keyword and sentence.</param>
/// <param name="Status"><c>failed</c> for a wrong, <c>error</c> for an exception.</param>
/// <param name="Failure">What the step threw, or null for a gathered wrong.</param>
public sealed record FailedStep(string Feature, string Scenario, string Text, ResultStatus Status, Exception? Failure)
{
    /// <summary>The specification identity, <c>{Feature}/{Scenario}</c>.</summary>
    public string Uid => $"{Feature}/{Scenario}";
}

/// <summary>
/// One attachment a scenario recorded: the reference every surface carries, and until the scenario
/// closes, the bytes.
/// </summary>
public sealed class RecordedAttachment
{
    internal RecordedAttachment(string name, string mediaType, long size, string? stepId, byte[]? content, string? omitted)
    {
        Name = name;
        MediaType = mediaType;
        Size = size;
        StepId = stepId;
        Content = content;
        Omitted = omitted;
    }

    public string Name { get; }
    public string MediaType { get; }

    /// <summary>Size in bytes.</summary>
    public long Size { get; }

    /// <summary>The step it hangs on, or null for the scenario.</summary>
    public string? StepId { get; }

    /// <summary>Why the bytes were not kept — over the size cap, a file that was not there — or null.</summary>
    public string? Omitted { get; }

    /// <summary>
    /// Where the file was written, relative to the results directory with <c>/</c> separators, or
    /// null when it was not written to one.
    /// </summary>
    public string? RelativePath { get; internal set; }

    /// <summary>The absolute path of the written file, or null.</summary>
    public string? FullPath { get; internal set; }

    /// <summary>The bytes, held only until the scenario closes and they are written out.</summary>
    internal byte[]? Content { get; set; }
}

internal static class MediaTypes
{
    private static readonly Dictionary<string, string> byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4"
    };

    public static string FromFileName(string path)
        => byExtension.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
