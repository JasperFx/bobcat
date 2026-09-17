namespace Bobcat.EventModel;

/// <summary>
/// The one question about a curated slice that both the reader and the scaffolder have to answer
/// the same way: does this slice answer over <b>HTTP</b>?
/// </summary>
/// <remarks>
/// <para>
/// It lived inside <c>SliceScaffolder.PlanFor</c> until issue #337 needed it at read time too —
/// <c>refusedWith:</c> states an HTTP status, and a bus-dispatched slice refuses by throwing, so
/// the field is a mistake there and the reader is where a mistake in the file is named. Copying
/// the predicate would have made the format's rule and the scaffolder's shape able to drift
/// silently, which is the failure mode <c>SliceTagParsingAgreementTests</c> and
/// <c>ResourceParsingAgreementTests</c> exist to prevent elsewhere; this one can simply be shared,
/// because the scaffolding assembly already references this one.
/// </para>
/// <para>
/// <b>Declared, not inferred.</b> The answer comes from what the model says — a Command slice
/// whose trigger is Http or Human — and nothing else. Both endpoint shapes the scaffolder emits
/// (the collapsed endpoint and the two-hop translation) are covered by it, which is what makes it
/// exactly <c>SlicePlan.OverHttp</c>.
/// </para>
/// </remarks>
public static class CuratedSliceShape
{
    /// <summary>
    /// Whether this slice's code is an HTTP endpoint — a Command slice triggered by Http or by a
    /// Human. Every other slice takes its trigger off the bus.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, like every other enum-valued field in the format
    /// (<c>CuratedModelReader.validateEnum</c> and <c>CuratedModelMapper.parse</c> both ignore
    /// case). The predicate this replaced compared ordinally, so a file writing
    /// <c>pattern: command</c> validated and mapped as a Command slice and then scaffolded as a
    /// bus handler — a disagreement nobody would have looked for.
    /// </remarks>
    public static bool AnswersOverHttp(CuratedSlice slice)
        => matches(slice.Pattern, "Command")
           && (matches(slice.Trigger?.Kind, "Http") || matches(slice.Trigger?.Kind, "Human"));

    private static bool matches(string? value, string word)
        => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);
}
