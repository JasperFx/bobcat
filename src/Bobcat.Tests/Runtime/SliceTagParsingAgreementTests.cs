using Bobcat.Generators;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Pins <see cref="GeneratorSliceTags"/> to <see cref="SliceTags"/>. Issue #106.
/// </summary>
/// <remarks>
/// <para>
/// The two exist because <c>Bobcat.Generators</c> is netstandard2.0 and references nothing — the
/// same constraint that stops it referencing Marten or EF for the persistence recipes. So the
/// <c>@slice:</c> / <c>@domain:</c> / <c>Triggered by</c> vocabulary is parsed twice.
/// </para>
/// <para>
/// A divergence would be expensive and silent: the runtime would report one slice name on
/// <c>FeatureDefinition.Slice</c> and the generated descriptor another, so run evidence (#107)
/// would fail to join to the design-time model with nothing anywhere reporting an error. Exactly
/// the failure <c>ResourceParsingAgreementTests</c> guards for the recovery-hint resource lists
/// that JasperFx and Bobcat each parse.
/// </para>
/// </remarks>
public class SliceTagParsingAgreementTests
{
    [Fact]
    public void the_prefixes_are_the_same_strings()
    {
        GeneratorSliceTags.SlicePrefix.ShouldBe(SliceTags.SlicePrefix);
        GeneratorSliceTags.DomainPrefix.ShouldBe(SliceTags.DomainPrefix);
        GeneratorSliceTags.TriggeredByPrefix.ShouldBe(SliceTags.TriggeredByPrefix);
    }

    public static TheoryData<string[]> TagSets
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add([]);
            data.Add(["slice:WithdrawFunds"]);
            data.Add(["domain:Banking"]);
            data.Add(["slice:WithdrawFunds", "domain:Banking"]);
            // Case, whitespace and empty values are where two hand-written parsers drift.
            data.Add(["SLICE:Shouty"]);
            data.Add(["slice:  padded  "]);
            data.Add(["slice:"]);
            data.Add(["slice"]);
            data.Add(["retry(2)", "isolated", "slice:Tagged"]);
            // First wins when a tag is repeated.
            data.Add(["slice:First", "slice:Second"]);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TagSets))]
    public void slice_and_domain_agree(string[] tags)
    {
        GeneratorSliceTags.Slice(tags).ShouldBe(SliceTags.Slice(tags));
        GeneratorSliceTags.Domain(tags).ShouldBe(SliceTags.Domain(tags));
    }

    public static TheoryData<string?> Descriptions
    {
        get
        {
            var data = new TheoryData<string?>();
            foreach (var description in new string?[]
                     {
                         null,
                         "",
                         "   ",
                         "Triggered by the account holder",
                         "Triggered by: the account holder",
                         "TRIGGERED BY the account holder",
                         "Triggered by",
                         "Triggered by   ",
                         "Some prose\nTriggered by the account holder\nMore prose",
                         "Some prose without a trigger line",
                         // Two trigger lines: whichever wins, both must pick the same one.
                         "Triggered by first\nTriggered by second"
                     })
            {
                data.Add(description);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void triggered_by_agrees(string? description)
    {
        GeneratorSliceTags.TriggeredBy(description).ShouldBe(SliceTags.TriggeredBy(description));
    }
}
