using System.Collections;
using System.Globalization;
using System.Text;

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
        => RenderWithValues(template, arguments).Text;

    /// <summary>
    /// The same rendering, plus <b>where the values landed</b> in the finished sentence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storyteller italicised a sentence's input cells, and it is worth copying: a step reads as
    /// prose, and the one thing a reader scans for is which parts of it were the data. Once the
    /// values are substituted into a flat string that information is gone, so it is carried out
    /// alongside the text rather than recovered later by searching for the values — which would
    /// mark the wrong run of characters whenever a value happens to appear in the prose too.
    /// </para>
    /// <para>
    /// Resolved in ONE pass over the template, which also fixes a smaller thing: substituting one
    /// argument at a time with <c>string.Replace</c> re-scanned text it had already written, so a
    /// value that itself looked like <c>{x}</c> could be substituted a second time.
    /// </para>
    /// </remarks>
    public static RenderedStepText RenderWithValues(string template, IReadOnlyList<StepArgument> arguments)
    {
        if (arguments.Count == 0 || template.IndexOf('{') < 0) return new RenderedStepText(template, []);

        var text = new StringBuilder(template.Length);
        var values = new List<StepTextSpan>();

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                text.Append(template[i]);
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // An unbalanced brace is text, not a placeholder.
                text.Append(template, i, template.Length - i);
                break;
            }

            var name = template.Substring(i + 1, close - i - 1);
            var rendered = resolve(name, arguments);

            if (rendered is null)
            {
                // The placeholder is the floor: a reader can see that something did not resolve,
                // rather than being shown a blank and believing it.
                text.Append(template, i, close - i + 1);
            }
            else
            {
                values.Add(new StepTextSpan(text.Length, rendered.Length, name));
                text.Append(rendered);
            }

            i = close;
        }

        return new RenderedStepText(text.ToString(), values);
    }

    private static string? resolve(string name, IReadOnlyList<StepArgument> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument.Name == name) return Value(argument.Value);
        }

        return null;
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

            // A table is the step's data, and a step's text is prose: it renders as a grid under the
            // sentence, so it has nothing to say inside it. The placeholder standing is the honest
            // outcome for a template that names it anyway.
            StepTable => null,

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

/// <summary>Where one substituted value sits in a rendered step sentence.</summary>
/// <param name="Start">0-based index into the rendered text.</param>
/// <param name="Length">Length of the value's rendering.</param>
/// <param name="Name">
/// The placeholder this span was substituted for — <c>sum</c> for <c>{sum}</c>. Null for a span from
/// a producer that did not record one.
/// </param>
/// <remarks>
/// The name is what lets a rendered cell find its place in the sentence: a comparison reported as
/// <c>Check("Sum", …)</c> belongs where <c>{sum}</c> was, and Storyteller rendered the verdict there
/// rather than on a line underneath. Matching by position would break the moment a template named
/// its placeholders in a different order from the checks.
/// </remarks>
public readonly record struct StepTextSpan(int Start, int Length, string? Name = null);

/// <summary>
/// A rendered step sentence and the spans of it that came from the step's arguments — its input
/// values, which a renderer shows in italics the way Storyteller did.
/// </summary>
public sealed record RenderedStepText(string Text, IReadOnlyList<StepTextSpan> Values);
