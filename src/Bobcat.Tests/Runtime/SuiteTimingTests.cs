using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #142: per-step aggregation is by normalized text, a fact about the grammar. The rest of
/// the analysis is pinned over projected recordings by <see cref="ProjectedSuiteTimingTests"/>.
/// </summary>
public class SuiteTimingTests
{
    [Theory]
    [InlineData("waits 30 seconds", "waits {number} seconds")]
    [InlineData("the amount is -2.5", "the amount is {number}")]
    [InlineData("hashed with sha256", "hashed with sha256")] // word-embedded digits stay
    [InlineData("version 1.2.3 is published", "version 1.2.3 is published")] // dotted versions stay
    public void normalization_folds_arguments_without_mangling_words(string text, string expected)
    {
        SuiteTiming.NormalizeStepText(text).ShouldBe(expected);
    }
}
