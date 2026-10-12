using System;
using System.Collections.Generic;

namespace Bobcat.Generators;

/// <summary>
/// The tag vocabulary the generator reads an Event Modeling slice binding from:
/// <c>slice:</c>, <c>domain:</c>, <c>chapter:</c> and <c>pattern:</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tags are produced inside the generator, never written by an author:
/// <see cref="MarkerCommentSpecs"/> renders a projected test's <c>[BobcatSlice]</c> /
/// <c>[BobcatSpec]</c> settings as tag strings (<c>slice:WithdrawFunds</c>,
/// <c>domain:BankAccount</c>), and <see cref="EventModelEmitter"/> reads them back here. Keeping
/// the attribute a typed front end over one small vocabulary means the emitter has a single place
/// to read a slice binding from. <c>TriggeredBy</c> is read separately, as prose (bobcat#463).
/// </para>
/// </remarks>
internal static class GeneratorSliceTags
{
    public const string SlicePrefix = "slice:";
    public const string DomainPrefix = "domain:";
    public const string ChapterPrefix = "chapter:";
    /// <summary>
    /// The slice's Event Modeling pattern, when the model states it (issue #323). Step text cannot
    /// tell Automation or Translation apart from Command — a bus command and an automation's
    /// trigger event read as the same sentence — so without this the spec source guessed Command
    /// for every automation and disagreed with the curated model that knew better. A declared
    /// pattern says what a sentence cannot.
    /// </summary>
    public const string PatternPrefix = "pattern:";

    public static string? Slice(IEnumerable<string> tags) => valueOf(tags, SlicePrefix);

    public static string? Domain(IEnumerable<string> tags) => valueOf(tags, DomainPrefix);

    public static string? Chapter(IEnumerable<string> tags) => valueOf(tags, ChapterPrefix);

    public static string? Pattern(IEnumerable<string> tags) => valueOf(tags, PatternPrefix);

    private static string? valueOf(IEnumerable<string> tags, string prefix)
    {
        foreach (var tag in tags)
        {
            if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var value = tag.Substring(prefix.Length).Trim();
            if (value.Length > 0) return value;
        }

        return null;
    }
}
