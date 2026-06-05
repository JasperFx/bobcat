namespace Bobcat.Engine.Verification;

/// <summary>
/// Compares an actual typed value against the expected text drawn from a spec.
/// Implementations own both the parse-expected-to-<typeparamref name="T"/> step and the
/// typed comparison, returning a <see cref="CheckResult"/> with formatted strings.
/// </summary>
/// <typeparam name="T">The static type of the value being checked.</typeparam>
public interface IValueChecker<in T>
{
    /// <summary>
    /// Compare <paramref name="actual"/> against <paramref name="expectedText"/>.
    /// </summary>
    /// <param name="actual">The value produced by the system under test.</param>
    /// <param name="expectedText">The raw expected text from the spec (already token-resolved).</param>
    /// <param name="options">Comparison options (tolerance, case sensitivity, trimming).</param>
    CheckResult Check(T actual, string expectedText, CheckOptions options);
}
