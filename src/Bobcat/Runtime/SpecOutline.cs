using System.Text.Json;

namespace Bobcat.Runtime;

/// <summary>
/// One specification's planned steps, as a preview shows them before anything has run (bobcat#644):
/// its identity, and each step as a line (<c>Given a proposed appointment</c>).
/// </summary>
/// <remarks>
/// <para>
/// What a resident runner registers beside its identities, so a console can list a suite's
/// specifications and what they say before a single one has run. The <c>bobcat preview</c> command
/// renders the same plan to a terminal.
/// </para>
/// <para>
/// <b>Written at process exit, not beside the manifest.</b> The manifest is written from the
/// declared-steps module initializer, and the planned steps of <c>[BobcatStep]</c> calls register
/// from a second initializer whose order relative to the first is the compiler's choice. By the
/// time the process exits every initializer has run, which is the moment the terminal preview
/// already renders at.
/// </para>
/// <para>
/// Templates, not sentences: a step shows <c>{value}</c> where an argument goes, because no argument
/// has been evaluated (<see cref="ProjectedSpecPreview"/>'s rule).
/// </para>
/// </remarks>
public sealed record SpecOutline(string Identity, IReadOnlyList<string> Steps)
{
    /// <summary>
    /// The file a listing writes the suite's outlines to, at process exit. Set by a resident runner
    /// beside <see cref="SpecManifest.PathVariable"/>; nothing is written without it.
    /// </summary>
    public const string PathVariable = "BOBCAT_LIST_OUTLINE";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private static int _armed;

    /// <summary>Every specification this process registered, with its planned steps.</summary>
    public static IReadOnlyList<SpecOutline> All()
        => ProjectedSpecPreview.All()
            .Select(spec => new SpecOutline(
                string.IsNullOrEmpty(spec.FeatureTitle) ? spec.Title : $"{spec.FeatureTitle}/{spec.Title}",
                spec.Steps
                    .Select(step => string.IsNullOrWhiteSpace(step.Keyword) ? step.StepText : $"{step.Keyword} {step.StepText}")
                    .ToList()))
            .ToList();

    /// <summary>
    /// When <see cref="PathVariable"/> names a file, write <see cref="All"/> to it as the process exits.
    /// Idempotent, and never fails the suite: an outline is a convenience for a console.
    /// </summary>
    public static void ArmIfRequested()
    {
        var path = Environment.GetEnvironmentVariable(PathVariable);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Interlocked.Exchange(ref _armed, 1) == 1) return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Write(path, All());
    }

    /// <summary>Write <paramref name="outlines"/> to <paramref name="path"/>; false, never a throw, when it cannot.</summary>
    public static bool Write(string path, IReadOnlyList<SpecOutline> outlines)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(outlines, _json));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The outlines a listing wrote, or an empty list when it wrote none or they cannot be read.</summary>
    public static IReadOnlyList<SpecOutline> Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<SpecOutline>>(File.ReadAllText(path), _json) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
