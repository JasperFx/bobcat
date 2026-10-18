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
    // bobcat#682: a Guid route was one group per spec
    [InlineData("DELETE /api/todolists/ffb2ea15-6c1d-4a5e-9b0f-3d2c1e0a9b8f", "DELETE /api/todolists/{guid}")]
    [InlineData("DELETE /api/todolists/ffb2ea15…", "DELETE /api/todolists/{guid}")]
    public void normalization_folds_arguments_without_mangling_words(string text, string expected)
    {
        SuiteTiming.NormalizeStepText(text).ShouldBe(expected);
    }
}
