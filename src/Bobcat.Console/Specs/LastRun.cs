using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bobcat.Console.Specs;

/// <summary>
/// What the last <c>bobcat run</c> of a spec project asked for and what failed, kept in
/// <c>obj/bobcat/last-run.json</c> beside the project (bobcat#572) so <c>--failed</c> and
/// <c>--repeat</c> work without <c>--results</c>. <c>obj/</c> is already git-ignored and cleaned by
/// <c>dotnet clean</c>.
/// </summary>
internal sealed class LastRun
{
    public const string FileName = "last-run.json";

    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>The shape of this file; moved only by a breaking change.</summary>
    public int Version { get; set; } = 1;

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The test host's exit code.</summary>
    public int ExitCode { get; set; }

    /// <summary>
    /// Whether the run's verdicts were read from its results file. False when the host wrote none
    /// (it died before a run closed), and then <see cref="Failed"/> is not "nothing failed" but
    /// "nobody knows".
    /// </summary>
    public bool ResultsRead { get; set; }

    /// <summary>The identities of the specifications that failed or errored, in the order the run reported them.</summary>
    public List<string> Failed { get; set; } = new();

    public LastRunArguments Arguments { get; set; } = new();

    /// <summary>Reserved for seeded random order (C8); always null until that ships, so <c>--repeat</c> needs no format change.</summary>
    public int? Seed { get; set; }

    public static string PathFor(SpecProject project)
        => Path.Combine(project.Directory, "obj", "bobcat", FileName);

    /// <summary>The file at <paramref name="path"/>, or null when there is none or it cannot be read.</summary>
    public static LastRun? Read(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<LastRun>(File.ReadAllText(path), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }

    /// <summary>
    /// The identities of every scenario in <paramref name="resultsFiles"/> that did not succeed —
    /// failed or errored, which the results file does not tell apart at this level and
    /// <c>--failed</c> does not need to. A scenario with no identity cannot be asked for again,
    /// so it is left out.
    /// </summary>
    public static List<string> FailedIn(IEnumerable<string> resultsFiles)
    {
        var failed = new List<string>();

        foreach (var file in resultsFiles)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("scenarios", out var scenarios)
                || scenarios.ValueKind != JsonValueKind.Array) continue;

            foreach (var scenario in scenarios.EnumerateArray())
            {
                var succeeded = scenario.TryGetProperty("succeeded", out var s) && s.ValueKind == JsonValueKind.True;
                if (succeeded) continue;

                // A failing Acceptance spec (bobcat#545) failed nothing: the platform reported it
                // skipped and the run's exit code is green, so it is not one of the run's failures.
                if (scenario.TryGetProperty("verdict", out var verdict)
                    && verdict.GetString() == SpecLifecycle.FailingVerdict) continue;

                if (scenario.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } identity
                    && !failed.Contains(identity, StringComparer.Ordinal))
                {
                    failed.Add(identity);
                }
            }
        }

        return failed;
    }
}

/// <summary>The invocation <c>--repeat</c> replays: its selection and the flags that shape a run.</summary>
internal sealed class LastRunArguments
{
    public string? Framework { get; set; }
    public string Configuration { get; set; } = "Debug";

    /// <summary>The identities the run was narrowed to; empty means every specification.</summary>
    public List<string> Selection { get; set; } = new();

    public string? Results { get; set; }
    public bool Json { get; set; }
    public bool Verbose { get; set; }
}
