namespace Bobcat.EventModel;

/// <summary>
/// The field-type vocabulary of the curated format, and the one place that decides what a
/// <c>fields:</c> sketch means (issue #318).
/// </summary>
/// <remarks>
/// <para>
/// A sketch is read two ways depending on where it appears, and the distinction is the whole
/// point. In <c>elements: … fields:</c> the author is naming a <b>type</b>; in a scenario's
/// <c>with:</c> or <c>contains:</c> they are giving a <b>sample value</b> and the type is inferred
/// from it. Falling back to <c>string</c> is correct for a sample and wrong for a declaration —
/// which is why <see cref="TryInfer"/> reports the fall-through instead of swallowing it, and the
/// reader turns it into a warning only for declarations.
/// </para>
/// <para>
/// This lives beside <see cref="CuratedModelFile"/> rather than in the scaffolder because both the
/// reader (to warn) and the scaffolder (to emit) must answer "is this a type I know" the same way.
/// Two copies of the list is two opinions about the same file.
/// </para>
/// </remarks>
public static class CuratedFieldTypes
{
    /// <summary>The scenario's own stream id, expanded by the feature writer (issue #235).</summary>
    public const string StreamIdToken = "{streamId}";

    /// <summary>
    /// Every type a <c>fields:</c> sketch may name, mapped from any casing to the spelling that
    /// compiles. The canonical form matters: a field declared <c>GUID</c> used to be emitted
    /// verbatim as <c>public GUID Foo</c>, which does not compile.
    /// </summary>
    private static readonly Dictionary<string, string> Canonical =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Guid"] = "Guid",
            ["int"] = "int",
            ["long"] = "long",
            ["bool"] = "bool",
            ["string"] = "string",
            ["decimal"] = "decimal",
            ["double"] = "double",
            ["DateTimeOffset"] = "DateTimeOffset",
            ["DateOnly"] = "DateOnly",
            ["TimeSpan"] = "TimeSpan",
        };

    /// <summary>The type names a declaration may use, in their canonical spelling.</summary>
    public static IReadOnlyCollection<string> Known => Canonical.Values;

    public static bool IsStreamIdToken(string value) =>
        value.Trim().Equals(StreamIdToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The type a sketch denotes, or false when nothing recognised it. A false here is what the
    /// reader warns about for a declared field and deliberately ignores for a sample value.
    /// </summary>
    public static bool TryInfer(string sketch, out string type)
    {
        // Must precede the sample-value inference: a literal "{streamId}" parses as nothing and
        // would type the identity field as a string.
        if (IsStreamIdToken(sketch))
        {
            type = "Guid";
            return true;
        }

        if (Canonical.TryGetValue(sketch, out var canonical))
        {
            type = canonical;
            return true;
        }

        if (Guid.TryParse(sketch, out _)) { type = "Guid"; return true; }
        if (bool.TryParse(sketch, out _)) { type = "bool"; return true; }
        if (int.TryParse(sketch, out _)) { type = "int"; return true; }
        if (decimal.TryParse(sketch, out _)) { type = "decimal"; return true; }
        if (DateTimeOffset.TryParse(sketch, out _)) { type = "DateTimeOffset"; return true; }

        type = "string";
        return false;
    }

    /// <summary>The type a sketch denotes, falling back to <c>string</c>. Correct for a sample
    /// value; for a declaration prefer <see cref="TryInfer"/> so the silence can be reported.</summary>
    public static string Infer(string sketch)
    {
        TryInfer(sketch, out var type);
        return type;
    }
}
