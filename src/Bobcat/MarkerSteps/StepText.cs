using System.Collections;
using System.Globalization;

namespace Bobcat;

/// <summary>
/// One argument a <c>[BobcatStep]</c> helper was called with, as the generated interceptor hands
/// it to <see cref="ScenarioRecorder"/> (issue #339).
/// </summary>
/// <param name="Name">The parameter's name — what a <c>{placeholder}</c> in the template matches.</param>
/// <param name="Value">The value at the call site, as it actually is at run time.</param>
public readonly record struct StepArgument(string Name, object? Value);

/// <summary>
/// Renders a <c>[BobcatStep]</c> template against the values the helper was called with
/// (issue #339).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why from the value and not from the call-site syntax.</b> The feature was designed for the
/// DaemonContext shape — <c>PublishMultiThreaded(3)</c> → "the events are published on 3 threads"
/// — where every argument is a literal, and only a literal could be substituted. A typed store
/// vocabulary is the opposite by nature: its arguments are types, minted ids and constructed
/// command objects. Measured on CritterCrush's projected lane, 73 of 95 generated step texts
/// carried a raw <c>{placeholder}</c>, and the canvas showed <c>{event} is emitted</c> sixteen
/// times. The more faithfully the vocabulary was built, the less of it rendered.
/// </para>
/// <para>
/// <b>The interceptor already holds every argument</b> — it has to, to forward the call — so
/// substituting at execution time costs one array and is also when a step's real data is worth
/// showing. It closes the workaround loop #324 left behind, too: a generic helper cannot be
/// intercepted (CS0411), so <c>GivenEvents&lt;Appointment&gt;(id)</c> has to become
/// <c>GivenEvents(typeof(Appointment), id)</c> — and <c>typeof(Appointment)</c> was exactly the
/// argument shape that could not bind.
/// </para>
/// <para>
/// <b>A sentence, not a dump.</b> Scalars render as their values; anything else renders as its
/// TYPE name, so <c>new ConfirmAppointment(id)</c> is "ConfirmAppointment" rather than a record's
/// whole <c>ToString</c>. A step's text is prose on a canvas — the data belongs in a table.
/// </para>
/// <para>
/// <b>The placeholder is the floor.</b> A value that renders to nothing — null, an empty sequence
/// — leaves <c>{name}</c> as written, which is today's behaviour and the honest one: a reader can
/// see that something was not resolved, rather than being shown a blank or the word "null" and
/// believing it.
/// </para>
/// </remarks>
public static class StepText
{
    /// <summary>
    /// The template with every <c>{name}</c> it can resolve replaced by the corresponding
    /// argument's rendering.
    /// </summary>
    public static string Render(string template, IReadOnlyList<StepArgument> arguments)
    {
        if (arguments.Count == 0) return template;

        foreach (var argument in arguments)
        {
            var placeholder = "{" + argument.Name + "}";
            if (!template.Contains(placeholder)) continue;

            if (Value(argument.Value) is { } rendered)
            {
                template = template.Replace(placeholder, rendered);
            }
        }

        return template;
    }

    /// <summary>
    /// How one value reads inside a step sentence, or null when it has nothing to say and the
    /// placeholder should stand.
    /// </summary>
    /// <remarks>
    /// Culture-invariant on purpose: a step's text travels to a report, a canvas and a wire event,
    /// and a number that reads differently per machine would make two runs of one scenario look
    /// like two scenarios.
    /// </remarks>
    public static string? Value(object? value)
        => value switch
        {
            null => null,

            // The type-capture case, and the reason this exists: a {aggregate}/{command}/{event}
            // helper takes a System.Type because a generic one cannot be intercepted.
            Type type => type.Name,

            string text => text.Length == 0 ? null : text,

            bool flag => flag ? "true" : "false",
            char character => character.ToString(),
            Enum @enum => @enum.ToString(),
            Guid id => id.ToString(),
            DateTime at => at.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset at => at.ToString("O", CultureInfo.InvariantCulture),
            DateOnly on => on.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly at => at.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan span => span.ToString(null, CultureInfo.InvariantCulture),
            IFormattable number when isNumeric(number) => number.ToString(null, CultureInfo.InvariantCulture),

            IEnumerable sequence => sequenceOf(sequence),

            // Everything else — a command record, an entity, a fixture — by the name of its type.
            _ => value.GetType().Name
        };

    private static bool isNumeric(object value)
        => value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or nint or nuint;

    /// <summary>
    /// A sequence as the names of what is in it — <c>ThenEvents(a, b)</c> reads "a, b are
    /// emitted". Capped, because a step is a sentence and a hundred-element list is not one.
    /// </summary>
    private static string? sequenceOf(IEnumerable sequence)
    {
        const int cap = 5;

        var rendered = new List<string>();
        var extra = 0;

        foreach (var item in sequence)
        {
            if (rendered.Count == cap)
            {
                extra++;
                continue;
            }

            if (Value(item) is { } text) rendered.Add(text);
        }

        if (rendered.Count == 0) return null;

        return extra > 0
            ? string.Join(", ", rendered) + $", and {extra} more"
            : string.Join(", ", rendered);
    }
}
