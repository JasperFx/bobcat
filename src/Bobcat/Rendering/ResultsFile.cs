using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Bobcat.Monitoring;
using Bobcat.Runtime;

namespace Bobcat.Rendering;

/// <summary>
/// The JSON results file: one file per run holding every scenario's <see cref="SpecRender"/>,
/// written when the run bracket closes (issue #540).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Without a console listening, a CI run used to leave console output and
/// TRX, and TRX knows nothing about steps, cells or tables. This is the persisted artifact an
/// agent or a CI job reads afterwards, and the source the HTML results page (#541) renders from.
/// </para>
/// <para>
/// <b>Opt in with <see cref="EnvironmentVariable"/></b>, naming a directory, so plain
/// <c>dotnet test</c> can ask for it; <c>bobcat run --results &lt;dir&gt;</c> and <c>--json</c>
/// set it for the host they launch. It is read per bracket, like the rest of the run info.
/// </para>
/// <para>
/// <b>Written per run bracket, not per process.</b> <see cref="MarkerStepRun.CloseRun"/> is the
/// moment, so a warm process serving several requests writes one file per request, and a runner
/// with no session hook writes one at process exit, which for it is the run.
/// </para>
/// <para>
/// <b>Reports follow <see cref="Engine.ScenarioReportVisibility"/></b> exactly as the console and
/// the wire do: a passing scenario's reports are in the file only when the run was verbose
/// (<c>--verbose</c>, <c>BOBCAT_VERBOSE_REPORTS</c>).
/// </para>
/// <para>
/// <b>The file carries a JSON Schema</b>, generated from the records it is written from
/// (<see cref="GenerateSchema"/>), committed beside this class, drift-tested, embedded in the
/// assembly and packed under <c>schemas/</c>. The schema is written into the results directory
/// next to the file, and the file's <c>$schema</c> names it, so an archived artifact describes
/// itself with no network.
/// </para>
/// <para>
/// <b>Writing can never fail a suite</b> — the invariant every other output here keeps. A
/// directory that cannot be created or written is a line on standard error, and the run goes on.
/// </para>
/// </remarks>
public static class ResultsFile
{
    /// <summary>A directory to write the run's results file into. Unset writes nothing.</summary>
    public const string EnvironmentVariable = "BOBCAT_RESULTS";

    /// <summary>The version of the results file's shape. Additive changes keep it; a breaking one moves it.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>The schema's file name, in the package under <c>schemas/</c> and beside every results file.</summary>
    public const string SchemaFileName = "bobcat-results-1.0.schema.json";

    private const string schemaResource = "Bobcat.Rendering." + SchemaFileName;

    private static readonly object _gate = new();
    private static readonly List<SpecRender> _specs = new();
    private static readonly List<ProjectedScenarioSample> _timings = new();
    private static bool _subscribed;
    private static string? _directory;
    private static MonitorRunInfo? _run;
    private static DateTimeOffset _startedAt;

    /// <summary>The directory <see cref="EnvironmentVariable"/> names, or null when unset.</summary>
    public static string? RequestedDirectory
        => Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } directory
            ? directory.Trim()
            : null;

    /// <summary>
    /// The directory the open bracket writes into, and its run id, or null when no results file was
    /// asked for. Attachments (issue #571) are written under it as each scenario closes.
    /// </summary>
    internal static (string Directory, Guid RunId)? Active
    {
        get
        {
            lock (_gate) return _run is null || _directory is null ? null : (_directory, _run.RunId);
        }
    }

    /// <summary>The committed schema, as shipped in the assembly.</summary>
    public static string SchemaText
    {
        get
        {
            using var stream = typeof(ResultsFile).Assembly.GetManifestResourceStream(schemaResource)
                               ?? throw new InvalidOperationException($"The embedded resource {schemaResource} is missing");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Start collecting for a run bracket, when <see cref="EnvironmentVariable"/> asks. Called by
    /// <see cref="MarkerStepRun"/> as the bracket opens.
    /// </summary>
    internal static void BeginRun(MonitorRunInfo run) => BeginRun(run, RequestedDirectory);

    internal static void BeginRun(MonitorRunInfo run, string? directory)
    {
        lock (_gate)
        {
            _specs.Clear();
            _timings.Clear();
            _directory = directory;
            _run = directory is null ? null : run;
            _startedAt = DateTimeOffset.UtcNow;

            if (_run is null || _subscribed) return;
            _subscribed = true;
            ScenarioRecorder.ScenarioCompleted += capture;
        }
    }

    /// <summary>
    /// Write the bracket's results file, when one was asked for. Returns its path, or null when
    /// nothing was asked for or the write failed.
    /// </summary>
    internal static string? EndRun()
    {
        MonitorRunInfo run;
        string directory;
        List<SpecRender> specs;
        List<ProjectedScenarioSample> timings;
        DateTimeOffset startedAt;

        lock (_gate)
        {
            if (_run is null || _directory is null) return null;

            run = _run;
            directory = _directory;
            specs = _specs.ToList();
            timings = _timings.ToList();
            startedAt = _startedAt;

            _run = null;
            _directory = null;
            _specs.Clear();
            _timings.Clear();
        }

        try
        {
            var document = Build(run, startedAt, DateTimeOffset.UtcNow, specs, SuiteTiming.ForProjected(timings));
            return Write(directory, run, document);
        }
        catch (Exception e)
        {
            try
            {
                Console.Error.WriteLine($"Bobcat could not write its results file to {directory}: {e.Message}");
            }
            catch
            {
                // Nowhere to say it is not a reason to fail a run either.
            }

            return null;
        }
    }

    /// <summary>The whole file, as the model it is serialized from.</summary>
    internal static JsonResultsOutput Build(MonitorRunInfo run, DateTimeOffset startedAt, DateTimeOffset finishedAt,
        IReadOnlyList<SpecRender> specs, SuiteTiming? timing)
    {
        // Feature, then title -- the console's order, for the same reason: a runner's finishing
        // order changes between runs of an unchanged suite, and two files should diff cleanly.
        var ordered = specs
            .OrderBy(x => x.FeatureTitle ?? "", StringComparer.Ordinal)
            .ThenBy(x => x.Title, StringComparer.Ordinal)
            .ToList();

        var passed = ordered.Count(x => x.Succeeded);

        // A failing Acceptance spec (bobcat#545) is in the file in full, under its own verdict, and
        // fails nothing -- counted apart, as run_finished counts it.
        var acceptanceFailing = ordered.Count(x => x.AcceptanceFailing);

        return new JsonResultsOutput
        {
            Schema = SchemaFileName,
            SchemaVersion = SchemaVersion,
            Run = new JsonRunOutput
            {
                RunId = run.RunId,
                Suite = run.Suite,
                Mode = run.Mode,
                Repository = run.Repository,
                Branch = run.Branch,
                Tag = run.Tag,
                Command = run.Command,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                DurationMs = (long)(finishedAt - startedAt).TotalMilliseconds
            },
            Summary = new JsonSummaryOutput
            {
                Scenarios = ordered.Count,
                Passed = passed,
                Failed = ordered.Count - passed - acceptanceFailing,
                AcceptanceFailing = acceptanceFailing,
                Succeeded = ordered.All(x => !x.FailsTheRun),
                Rights = ordered.Sum(x => x.Counts.Rights),
                Wrongs = ordered.Sum(x => x.Counts.Wrongs),
                Errors = ordered.Sum(x => x.Counts.Errors)
            },
            Scenarios = ordered.Select(JsonRenderer.specToJson).ToList(),
            Timing = timing is null ? null : JsonRenderer.timingToJson(timing)
        };
    }

    /// <summary>The file's JSON text.</summary>
    internal static string Serialize(JsonResultsOutput document)
        => JsonSerializer.Serialize(document, JsonRenderer.options);

    /// <summary>
    /// Write <paramref name="document"/> into <paramref name="directory"/>, with the schema beside
    /// it. The name is <c>{suite}-{runId}.json</c>; a second file for the same run (supervised
    /// workers share a <c>BOBCAT_RUN_ID</c>) takes the next free <c>-2</c>, <c>-3</c>.
    /// </summary>
    internal static string Write(string directory, MonitorRunInfo run, JsonResultsOutput document)
    {
        Directory.CreateDirectory(directory);
        writeSchema(directory);

        var json = Serialize(document);
        var stem = $"{Safe(run.Suite)}-{run.RunId:N}";

        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(directory, attempt == 1 ? $"{stem}.json" : $"{stem}-{attempt}.json");
            try
            {
                // CreateNew, so two processes racing for one name cannot overwrite each other.
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(json);
                return Path.GetFullPath(path);
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Taken; try the next name.
            }
        }
    }

    private static void writeSchema(string directory)
    {
        var path = Path.Combine(directory, SchemaFileName);
        var schema = SchemaText;

        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == schema) return;
            File.WriteAllText(path, schema, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Another process writing the same schema at the same moment; its copy is this one.
        }
    }

    internal static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return cleaned.Length == 0 ? "bobcat" : cleaned;
    }

    private static void capture(ScenarioRecorder.Recording recording)
    {
        lock (_gate)
        {
            if (_run is null) return;
        }

        var spec = SpecRender.FromRecording(recording);
        var timing = ProjectedScenarioSample.From(recording);

        lock (_gate)
        {
            if (_run is null) return;
            _specs.Add(spec);
            _timings.Add(timing);
        }
    }

    /// <summary>
    /// The results file's JSON Schema, exported from the records the file is serialized from, with
    /// the options it is serialized with. The committed copy beside this class must equal it; a
    /// test fails when the two drift.
    /// </summary>
    public static string GenerateSchema()
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject obj) return node;

                var description = context.PropertyInfo?.AttributeProvider is MemberInfo member
                    ? member.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description
                    : null;
                if (description is not null) obj.Insert(0, "description", description);

                return node;
            }
        };

        var body = JsonRenderer.options.GetJsonSchemaAsNode(typeof(JsonResultsOutput), exporter).AsObject();

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Bobcat results file",
            ["description"] = $"One Bobcat run's specifications and their results. Schema version {SchemaVersion}."
        };

        foreach (var (key, value) in body.ToList())
        {
            body.Remove(key);
            root[key] = value;
        }

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Test seam: forget any open collection.</summary>
    internal static void Reset()
    {
        lock (_gate)
        {
            _run = null;
            _directory = null;
            _specs.Clear();
            _timings.Clear();
        }
    }
}

// The results file's own records. The scenarios inside are JsonRenderer's, so a scenario reads the
// same here as from RenderScenario.

internal class JsonResultsOutput
{
    [JsonPropertyName("$schema")]
    [System.ComponentModel.Description("The schema this file conforms to, written beside it.")]
    public string Schema { get; set; } = ResultsFile.SchemaFileName;

    [System.ComponentModel.Description("The version of this file's shape.")]
    public string SchemaVersion { get; set; } = ResultsFile.SchemaVersion;

    public JsonRunOutput Run { get; set; } = new();

    public JsonSummaryOutput Summary { get; set; } = new();

    [System.ComponentModel.Description("Every scenario the run finished, by feature then title. A skipped test is not here; a failing Acceptance specification, which the test platform reports skipped, is, with its verdict.")]
    public List<JsonScenarioOutput> Scenarios { get; set; } = new();

    [System.ComponentModel.Description("Where the run spent its time; absent when nothing was measured.")]
    public JsonTimingOutput? Timing { get; set; }
}

internal class JsonRunOutput
{
    [System.ComponentModel.Description("The run's id: the one on run_started, BOBCAT_RUN_ID when set.")]
    public Guid RunId { get; set; }

    [System.ComponentModel.Description("The spec assembly.")]
    public string Suite { get; set; } = "";

    [System.ComponentModel.Description("How it ran: xunit, tunit, nunit, mstest.")]
    public string Mode { get; set; } = "";

    public string Repository { get; set; } = "";
    public string? Branch { get; set; }

    [System.ComponentModel.Description("BOBCAT_RUN_TAG, verbatim.")]
    public string? Tag { get; set; }

    [System.ComponentModel.Description("The resident runner command that asked for the run.")]
    public string? Command { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public long DurationMs { get; set; }
}

internal class JsonSummaryOutput
{
    public int Scenarios { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }

    [System.ComponentModel.Description(
        "Acceptance specifications that are not yet passing (verdict \"acceptance: failing\"): counted in neither passed nor failed, and never the reason succeeded is false.")]
    public int AcceptanceFailing { get; set; }

    [System.ComponentModel.Description(
        "True when no scenario in the file failed the run: every one passed, or is an Acceptance specification not yet passing.")]
    public bool Succeeded { get; set; }

    public int Rights { get; set; }
    public int Wrongs { get; set; }
    public int Errors { get; set; }
}
