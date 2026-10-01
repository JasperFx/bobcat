namespace Bobcat;

/// <summary>
/// A step's stack trace with the frames nobody wants taken out — Bobcat's own plumbing, the test
/// runner's, and the reflection and async machinery in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is worth having.</b> A projected step's stack is five frames and one of them is the
/// answer:
/// </para>
/// <code>
/// InvalidOperationException: Cannot rename to 'Jerry' — the naming service is down
///   at async Task RenameFails(string name) in AsyncGrammar.cs:39            ← the only useful frame
///   at async Task Track(Task task, IDisposable step) in MarkerStepRuntime.cs:25
///   at async Task an_asynchronous_step_that_throws() in SentenceVariationSpecs.cs:120
///   at void MoveNext() in TestRunner.cs:170
///   at async ValueTask RunAsync(Func&lt;ValueTask&gt;) in ExceptionAggregator.cs:124
/// </code>
/// <para>
/// The generated interceptor frame is unavoidable — it is a consequence of C#'s interceptor feature,
/// not a choice — so the only place to deal with it is here.
/// </para>
/// <para>
/// <b>Filtered on TEXT, not on reflection.</b> Out of process a stack is a string and nothing else,
/// which is where a supervised worker's failures come from; a rule that needed
/// <c>MethodBase.DeclaringType</c> would work in one lane and not the other. The same reason
/// <c>FailureSignature</c> matches exception type names rather than types.
/// </para>
/// <para>
/// <b>Nothing is dropped silently, and never everything.</b> The count of hidden frames is reported
/// so a reader knows the stack was edited, and a trace where the filter would leave nothing is
/// returned whole — a stack with no visible frames is worse than a noisy one.
/// </para>
/// </remarks>
public static class SpecStackTrace
{
    /// <summary>
    /// Frame prefixes that are never the answer. Matched against the text after <c>at </c>.
    /// </summary>
    private static readonly string[] noise =
    [
        // Bobcat's own plumbing: the generated interceptor, the step bracket, the executor.
        "Bobcat.Generated.",
        "Bobcat.MarkerStepRuntime.",
        "Bobcat.ScenarioRecorder",
        "Bobcat.SpecAssert.",
        "Bobcat.Engine.Executor",
        "Bobcat.Engine.DelegateExecutionStep",
        "Bobcat.Runtime.BobcatRunner",

        // The test runners.
        "Xunit.",
        "TUnit.",
        "NUnit.",
        "Microsoft.VisualStudio.TestTools.",
        "Microsoft.Testing.",

        // Reflection, async and rethrow machinery.
        "System.Reflection.",
        "System.Runtime.CompilerServices.",
        "System.Runtime.ExceptionServices.",
        "System.Threading.Tasks.",
        "System.Threading.ExecutionContext"
    ];

    /// <summary>A trace's frames worth reading, and how many were left out.</summary>
    /// <param name="Frames">The surviving lines, trimmed, in order.</param>
    /// <param name="Hidden">How many frames the filter removed. Zero when it removed none.</param>
    public readonly record struct Filtered(IReadOnlyList<string> Frames, int Hidden);

    /// <summary>Filter <paramref name="stackTrace"/>. An empty trace filters to nothing.</summary>
    public static Filtered Filter(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return new Filtered([], 0);

        var frames = new List<string>();
        var kept = new List<string>();

        foreach (var raw in stackTrace!.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // "--- End of stack trace from previous location ---" and friends. Structural noise that
            // says nothing once the frames around it are gone.
            if (line.StartsWith("---", StringComparison.Ordinal)) continue;

            frames.Add(line);
            if (!isNoise(line)) kept.Add(line);
        }

        // A stack with no visible frames is worse than a noisy one: the reader learns nothing and
        // cannot even tell that a filter happened.
        return kept.Count == 0
            ? new Filtered(frames, 0)
            : new Filtered(kept, frames.Count - kept.Count);
    }

    /// <summary>Filter an exception's own stack.</summary>
    public static Filtered Filter(Exception? exception) => Filter(exception?.StackTrace);

    /// <summary>
    /// One frame shortened for READING: the type without its namespace, and the file without its path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presentation, not knowledge — which is why it is separate from <see cref="Filter"/> and why the
    /// wire carries the unshortened frames. Whether to abbreviate is a viewer's decision about its own
    /// width; which frames are Bobcat's own plumbing is not.
    /// </para>
    /// <para>
    /// The absolute path is what makes the difference in a terminal: one frame of
    /// <c>/Users/…/src/Bobcat.Xunit.Samples/Grammars/AsyncGrammar.cs:line 39</c> wraps over three lines
    /// and buries the two frames that matter.
    /// </para>
    /// </remarks>
    public static string Shorten(string frame)
    {
        var text = frame;

        // "in <path>:line 39" → "in AsyncGrammar.cs:39"
        var inAt = text.LastIndexOf(" in ", StringComparison.Ordinal);
        if (inAt > 0)
        {
            var location = text.Substring(inAt + 4);
            var file = location;
            var lineAt = location.LastIndexOf(":line ", StringComparison.Ordinal);
            var lineNumber = "";

            if (lineAt > 0)
            {
                file = location.Substring(0, lineAt);
                lineNumber = ":" + location.Substring(lineAt + 6);
            }

            var slash = file.LastIndexOfAny(['/', '\\']);
            if (slash >= 0) file = file.Substring(slash + 1);

            text = text.Substring(0, inAt) + " in " + file + lineNumber;
        }

        // "at Some.Long.Namespace.Type.Method(args)" → "at Type.Method(args)"
        var paren = text.IndexOf('(');
        var head = paren > 0 ? text.Substring(0, paren) : text;
        var tail = paren > 0 ? text.Substring(paren) : "";

        var prefix = head.StartsWith("at ", StringComparison.Ordinal) ? "at " : "";
        var name = head.Substring(prefix.Length);

        var parts = name.Split('.');
        if (parts.Length > 2) name = parts[parts.Length - 2] + "." + parts[parts.Length - 1];

        return prefix + name + tail;
    }

    private static bool isNoise(string frame)
    {
        var at = frame.StartsWith("at ", StringComparison.Ordinal) ? frame.Substring(3) : frame;

        // A frame reads "at [async Task ]Namespace.Type.Method(...)", and the return type in front is
        // there on .NET's newer formatting. The type name is whatever sits before the first '(' — scan
        // the whole remainder rather than only its start, so both spellings match.
        foreach (var prefix in noise)
        {
            if (at.StartsWith(prefix, StringComparison.Ordinal)) return true;
            if (at.Contains(" " + prefix)) return true;
        }

        return false;
    }
}
