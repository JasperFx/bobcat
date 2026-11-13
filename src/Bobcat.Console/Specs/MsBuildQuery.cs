namespace Bobcat.Console.Specs;

/// <summary>
/// How the tool asks MSBuild a question (<c>dotnet msbuild &lt;project&gt; -getProperty:…</c>) and reads
/// the answer off stdout without being fooled by what the .NET CLI prints there first (bobcat#830).
/// </summary>
/// <remarks>
/// <para>
/// <b>The CLI writes its own notices to stdout, ahead of the value.</b> On a busy box the SDK's MSBuild
/// server can fail to start, and the CLI then prints <i>"MSBuild server unavailable: could not connect
/// to the server within the timeout window; the server may have failed to start. Falling back to …"</i>
/// — on <b>stdout</b>, before running the query in-process and printing the real answer. Read naively,
/// that sentence became the test host's path (<c>bobcat resident</c>: "there is no test host at 'MSBuild
/// server unavailable: …'") and broke the JSON of a multi-property query ("Could not read the target
/// frameworks"). It showed up on CI the moment the libraries grew a third target framework and the
/// fleet got busier; <c>main</c>'s runs had never printed it.
/// </para>
/// <para>
/// Two defences, both deliberate. <see cref="Environment"/> turns the server off for a property query
/// (<c>DOTNET_CLI_USE_MSBUILD_SERVER=0</c>: an evaluation has nothing to gain from a warm server), stops
/// the worker node lingering (<c>MSBUILDDISABLENODEREUSE=1</c>, as <c>Gate</c> does for its builds), and
/// silences the logo and telemetry notices, which are more stdout the value is not. And the readers
/// below still parse defensively — the last non-empty line for one property, the text from the first
/// <c>{</c> for the JSON several properties produce — because a future CLI notice should cost a
/// query nothing either.
/// </para>
/// </remarks>
internal static class MsBuildQuery
{
    /// <summary>The environment every <c>dotnet msbuild -getProperty</c> child runs with.</summary>
    public static IDictionary<string, string?> Environment { get; } = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    };

    /// <summary>
    /// The value of a single <c>-getProperty</c>: the last non-empty line of the output, trimmed. MSBuild
    /// prints the value last and alone; anything above it is the CLI talking. Empty when nothing was
    /// printed.
    /// </summary>
    public static string Property(string output)
    {
        var lines = output.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0) return line;
        }

        return "";
    }

    /// <summary>
    /// The JSON several <c>-getProperty</c> switches produce: the output from its first <c>{</c>, so a
    /// notice printed ahead of it is dropped. The output unchanged when it has no <c>{</c>, so the
    /// caller's parse fails the way it always did and its error still shows what was printed.
    /// </summary>
    public static string Json(string output)
    {
        var start = output.IndexOf('{');
        return start < 0 ? output : output[start..];
    }
}
