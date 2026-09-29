using Microsoft.CodeAnalysis;

namespace Bobcat.Generators;

/// <summary>
/// The one place the generator decides "is this a step, and what keyword is it" — for both
/// authoring lanes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one place.</b> There used to be two vocabularies: <c>[Given]/[When]/[Then]/[Check]</c>
/// matched against a <c>.feature</c> file, and <c>[BobcatStep]</c> intercepted at a C# call site.
/// The split had no reason behind it — a step's text and its keyword are the same two facts however
/// the step is reached — and it cost a grammar author a second set of attributes to learn and a
/// second copy of every step to maintain if they wanted both.
/// </para>
/// <para>
/// <b>Read from the attribute's TYPE, never by instantiating it.</b> A generator cannot run a
/// consumer's code, and it does not need to: the keyword is a property of the class. The base chain
/// is walked, so a project's own <c>[GivenEvents]</c> deriving from <c>GivenAttribute</c> inherits
/// the keyword with nothing registered anywhere.
/// </para>
/// </remarks>
internal static class StepAttributes
{
    /// <summary>The attribute type names that fix a keyword, and the keyword each fixes.</summary>
    private static readonly (string Attribute, string Keyword)[] known =
    [
        ("GivenAttribute", "Given"),
        ("WhenAttribute", "When"),

        // [Check] before [Then]: a method may carry both (the documented way to make a check
        // navigable in an editor that only knows Given/When/Then), and Check is the stronger claim —
        // it asserts on the bool, where Then would discard it.
        ("CheckAttribute", "Check"),
        ("ThenAttribute", "Then"),

        // The keywordless base, and the legacy projected-lane spelling. Both may still carry an
        // explicit Keyword as a named argument, which is read separately.
        ("BobcatStepAttribute", ""),
        ("StepAttribute", "")
    ];

    internal sealed class Recognized
    {
        public string Keyword = "";
        public string Expression = "";

        /// <summary>
        /// True when the keyword came from the attribute's own <c>Keyword = "…"</c> argument rather
        /// than from its type. Kept because that is the one case where the author said the keyword
        /// out loud, and a diagnostic should quote it back to them.
        /// </summary>
        public bool KeywordWasExplicit;
    }

    /// <summary>
    /// What <paramref name="attribute"/> declares, or null when it is not a step attribute at all.
    /// </summary>
    public static Recognized? Recognize(AttributeData attribute)
    {
        var keyword = keywordOf(attribute.AttributeClass);
        if (keyword is null) return null;

        var expression = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value?.ToString() ?? ""
            : "";

        var recognized = new Recognized { Keyword = keyword, Expression = expression };

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key != "Keyword") continue;
            if (named.Value.Value is not string explicitKeyword || explicitKeyword.Length == 0) continue;

            recognized.Keyword = explicitKeyword;
            recognized.KeywordWasExplicit = true;
        }

        return recognized;
    }

    /// <summary>
    /// The best step attribute on <paramref name="method"/>, or null. "Best" is the strongest claim
    /// rather than the last one written, so the outcome never depends on attribute order.
    /// </summary>
    public static Recognized? On(IMethodSymbol method)
    {
        Recognized? best = null;
        var bestRank = int.MaxValue;

        foreach (var attribute in method.GetAttributes())
        {
            if (Recognize(attribute) is not { } recognized) continue;

            var rank = rankOf(attribute.AttributeClass);
            if (rank >= bestRank) continue;

            best = recognized;
            bestRank = rank;
        }

        return best;
    }

    private static string? keywordOf(INamedTypeSymbol? attributeClass)
    {
        for (var type = attributeClass; type is not null; type = type.BaseType)
        {
            foreach (var (name, keyword) in known)
            {
                if (type.Name == name) return keyword;
            }
        }

        return null;
    }

    /// <summary>
    /// How strong a claim an attribute makes — lower wins. A keyword named outright beats one
    /// inherited from a base, and <c>[Check]</c> beats <c>[Then]</c> for the reason above.
    /// </summary>
    private static int rankOf(INamedTypeSymbol? attributeClass)
    {
        var depth = 0;
        for (var type = attributeClass; type is not null; type = type.BaseType, depth++)
        {
            for (var i = 0; i < known.Length; i++)
            {
                if (type.Name == known[i].Attribute) return i * 100 + depth;
            }
        }

        return int.MaxValue;
    }
}
