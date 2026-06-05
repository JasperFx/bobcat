namespace Bobcat.Engine.Verification;

/// <summary>
/// Reserved cell tokens with type-agnostic meaning. To compare against the literal
/// strings <c>"NULL"</c>/<c>"EMPTY"</c>, wrap the expected value in double quotes.
/// </summary>
public static class CellTokens
{
    /// <summary>Expect a null actual value.</summary>
    public const string Null = "NULL";

    /// <summary>Expect an empty actual value (null, empty string, or empty collection).</summary>
    public const string Empty = "EMPTY";
}
