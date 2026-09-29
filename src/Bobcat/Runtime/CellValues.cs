using Bobcat.Engine;

namespace Bobcat.Runtime;

/// <summary>
/// The floor under the generator's value conversion: a written value that cannot be read as the
/// parameter it binds to.
/// </summary>
/// <remarks>
/// A value the generator cannot convert is <b>BOBCAT030</b> at build time, and the feature is
/// suppressed — so nothing here should ever run. It exists because the alternative floor was worse:
/// before BOBCAT030 the generator emitted the cell's text verbatim, and a specification that said
/// <c>Given a quantity of oops</c> failed the build with <c>CS0103: the name 'oops' does not exist</c>
/// in a generated file the author cannot open. If a binding path the validator does not walk ever
/// reaches here, the author gets a sentence naming their cell instead.
/// </remarks>
public static class CellValues
{
    /// <summary>
    /// Always throws. The signature returns <typeparamref name="T"/> so the call sits in the
    /// argument position the value would have occupied.
    /// </summary>
    public static T Unreadable<T>(string parameter, string value, string problem)
        => throw new SpecCriticalException(
            $"The value '{value}' for '{parameter}' could not be read"
            + (problem.Length > 0 ? $": {problem}" : "."));
}
