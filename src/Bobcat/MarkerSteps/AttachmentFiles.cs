using Bobcat.Rendering;

namespace Bobcat;

/// <summary>
/// Where a closing scenario's kept attachments go (issue #571): files beside the results file when
/// the run asked for one, the test platform's own attachments otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>One or the other, not both.</b> A run that writes results is read from its results directory
/// (an agent, a CI artifact, the HTML page), and copying every screenshot into TRX as well would
/// double the bytes for no reader. A plain <c>dotnet test</c> has nowhere else to put them.
/// </para>
/// <para>
/// <b>Layout:</b> <c>attachments/{runId}/{scenario}/{nn}-{name}</c> under the results directory,
/// <c>nn</c> the attachment's position in the scenario so two attachments of one name (a screenshot
/// per failed step) never collide. Created with <see cref="FileMode.CreateNew"/>, taking the next
/// free name, because supervised workers share a run id and a retried scenario closes twice.
/// </para>
/// <para>
/// <b>Writing can never fail a test</b> — the invariant every output here keeps.
/// </para>
/// </remarks>
internal static class AttachmentFiles
{
    internal const string DirectoryName = "attachments";

    public static void Write(ScenarioRecorder.Recording recording, IReadOnlyList<RecordedAttachment> attachments)
    {
        var results = ResultsFile.Active;
        var platform = SpecAttachment.PlatformSink;

        for (var i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            if (attachment.Content is not { } content) continue;

            try
            {
                if (results is { } active)
                {
                    writeFile(active.Directory, active.RunId, recording.Uid, i + 1, attachment, content);
                }
                else
                {
                    // Numbered as the files are: a platform keys attachments by name (xUnit refuses a
                    // second one), and a screenshot per failed step all share one.
                    platform?.Invoke($"{i + 1:00}-{attachment.Name}", attachment.MediaType, content);
                }
            }
            catch (Exception e)
            {
                SpecAttachment.Log($"Bobcat could not write the attachment '{attachment.Name}': {e.Message}");
            }
        }
    }

    private static void writeFile(string root, Guid runId, string uid, int position, RecordedAttachment attachment, byte[] content)
    {
        var relativeDirectory = Path.Combine(DirectoryName, runId.ToString("N"), ResultsFile.Safe(uid.Replace('/', '_')));
        var directory = Path.Combine(root, relativeDirectory);
        Directory.CreateDirectory(directory);

        var name = ResultsFile.Safe(attachment.Name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var attempt = 1; ; attempt++)
        {
            var file = attempt == 1
                ? $"{position:00}-{name}"
                : $"{position:00}-{stem}-{attempt}{extension}";
            var path = Path.Combine(directory, file);

            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(content);
                }

                attachment.FullPath = Path.GetFullPath(path);
                attachment.RelativePath = Path.Combine(relativeDirectory, file).Replace(Path.DirectorySeparatorChar, '/');
                return;
            }
            catch (IOException) when (File.Exists(path) && attempt < 100)
            {
                // Taken; try the next name.
            }
        }
    }
}
