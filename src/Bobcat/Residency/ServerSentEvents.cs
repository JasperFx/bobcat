using System.Runtime.CompilerServices;
using System.Text;

namespace Bobcat.Residency;

/// <summary>
/// One event off a <c>text/event-stream</c>.
/// </summary>
/// <param name="Id">
/// The stream position, or null when the event carried no <c>id:</c>. A reader remembers the last
/// one it saw and sends it back as <c>Last-Event-ID</c> on reconnect, which is how a dropped stream
/// resumes rather than losing whatever arrived while it was down.
/// </param>
/// <param name="Type">The <c>event:</c> name, or <c>message</c> — the spec's default — when absent.</param>
/// <param name="Data">Every <c>data:</c> line, joined with newlines, as the spec requires.</param>
public sealed record ServerSentEvent(string? Id, string Type, string Data);

/// <summary>
/// A reader for the subset of <c>text/event-stream</c> this wire uses (issue #390).
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-rolled rather than packaged.</b> <c>System.Net.ServerSentEvents</c> is in the box on
/// <b>.NET 10 only</b>, and Bobcat also targets net9.0 — so the choice was a package reference on
/// one target and a shared framework type on the other, or one implementation of the four fields
/// actually used. One implementation won: two code paths for a parser is two things to keep
/// agreeing, and this is forty lines testable against a canned stream.
/// </para>
/// <para>
/// <b>What it honours, and what it does not.</b> <c>id</c>, <c>event</c> and <c>data</c> (repeated
/// and joined), comment lines (a leading <c>:</c>, which is how a server keeps a connection alive
/// through a proxy), a single optional space after each field's colon, and the blank line that
/// dispatches an event. <c>retry</c> is parsed as a field and ignored, because the reconnect delay
/// here is the runner's own backoff — a monitor should not be able to make a runner reconnect in a
/// tight loop. A trailing event with no blank line after it is dispatched at end of stream, so a
/// server that closes cleanly does not lose its last message.
/// </para>
/// </remarks>
public static class ServerSentEvents
{
    /// <summary>The media type a command stream is requested and served as.</summary>
    public const string MediaType = "text/event-stream";

    /// <summary>The spec's default event name, for an event with no <c>event:</c> field.</summary>
    public const string DefaultType = "message";

    /// <summary>Reads events until the stream ends or the token is cancelled.</summary>
    public static async IAsyncEnumerable<ServerSentEvent> Read(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        string? id = null;
        string? type = null;
        var data = new StringBuilder();
        var any = false;

        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token);

            // End of stream. Anything part-way through is still an event — a server that closed
            // after writing one must not lose it.
            if (line is null) break;

            if (line.Length == 0)
            {
                if (any) yield return new ServerSentEvent(id, type ?? DefaultType, data.ToString());

                // id persists across events by the spec: a stream may send it once and then omit
                // it, and Last-Event-ID still has to be the latest one seen.
                type = null;
                data.Clear();
                any = false;
                continue;
            }

            // A comment, which is also how a keepalive usually arrives.
            if (line[0] == ':') continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];

            // "a single space after the colon is ignored" — and only one. A field with an empty
            // value ("data:") has nothing after the colon at all, which is legal and is how a
            // blank data line is written.
            var value = colon < 0 || colon + 1 >= line.Length
                ? ""
                : line[colon + 1] == ' ' ? line[(colon + 2)..] : line[(colon + 1)..];

            switch (field)
            {
                case "id":
                    // The spec says to ignore an id containing a NUL; nothing else is invalid.
                    if (!value.Contains('\0')) id = value;
                    any = true;
                    break;

                case "event":
                    type = value;
                    any = true;
                    break;

                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    any = true;
                    break;

                // retry is deliberately read and dropped — see the remarks.
                default:
                    break;
            }
        }

        if (any) yield return new ServerSentEvent(id, type ?? DefaultType, data.ToString());
    }
}
