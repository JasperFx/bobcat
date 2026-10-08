using Bobcat.Engine;
using Bobcat.Engine.Verification;
using Bobcat.Runtime;

namespace Bobcat.Partial;

/// <summary>
/// Converting a specified value to the member's type. Table text goes through
/// <see cref="CellValues.Read(string, Type)"/>, so <c>NULL</c>, <c>EMPTY</c> and relative times mean the
/// same here as in every other table; a typed value from code is used as it is wherever it fits.
/// </summary>
internal static class SpecifiedValues
{
    public static object? Convert(SpecifiedValue specified, Type target, string path)
        => specified.IsText
            ? ReadText((string?)specified.Value ?? "", target, path)
            : convertValue(specified.Value, target, path);

    /// <summary>
    /// Read a cell as <paramref name="target"/>, including the shapes a cell cannot name directly:
    /// an F# option (blank or <c>NULL</c> is <c>None</c>), an F# single-case union wrapping the value,
    /// and a collection written as a comma-separated list.
    /// </summary>
    public static object? ReadText(string text, Type target, string path)
    {
        var trimmed = text.Trim();

        if (FSharpShapes.OptionValueType(target) is { } inner)
        {
            return trimmed.Length == 0 || trimmed.Equals(CellTokens.Null, StringComparison.OrdinalIgnoreCase)
                ? FSharpShapes.None(target)
                : FSharpShapes.Some(target, ReadText(text, inner, path));
        }

        if (FSharpShapes.SingleCaseWrapper(target) is { } wrapper)
            return wrapper.Invoke(null, [ReadText(text, wrapper.GetParameters()[0].ParameterType, path)]);

        if (CollectionShapes.ElementType(target) is { } element)
        {
            if (trimmed.Equals(CellTokens.Null, StringComparison.OrdinalIgnoreCase)) return null;
            if (trimmed.Length == 0 || trimmed.Equals(CellTokens.Empty, StringComparison.OrdinalIgnoreCase))
                return CollectionShapes.Make(target, []);

            return CollectionShapes.Make(target,
                trimmed.Split(',').Select(item => ReadText(item, element, path)).ToList());
        }

        try
        {
            return CellValues.Read(text, target);
        }
        catch (BadCellException e)
        {
            throw new BadCellException($"'{path}': {e.Message}");
        }
    }

    private static object? convertValue(object? value, Type target, string path)
    {
        if (value is null)
        {
            if (FSharpShapes.OptionValueType(target) is not null) return FSharpShapes.None(target);
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                throw new BadCellException($"'{path}' is a {target.Name}, which cannot be null.");
            return null;
        }

        if (target.IsInstanceOfType(value)) return value;

        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying is not null) return convertValue(value, underlying, path);

        if (FSharpShapes.OptionValueType(target) is { } inner)
            return FSharpShapes.Some(target, convertValue(value, inner, path));

        if (FSharpShapes.SingleCaseWrapper(target) is { } wrapper)
            return wrapper.Invoke(null, [convertValue(value, wrapper.GetParameters()[0].ParameterType, path)]);

        if (value is string text) return ReadText(text, target, path);

        if (CollectionShapes.ElementType(target) is { } element && CollectionShapes.ItemsOf(value) is { } items)
            return CollectionShapes.Make(target, items.Select(item => convertValue(item, element, path)).ToList());

        try
        {
            if (target.IsEnum) return Enum.ToObject(target, value);
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
                return System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException or ArgumentException)
        {
            throw new BadCellException($"'{path}' is a {target.Name}, and {ScenarioValues.Format(value)} ({value.GetType().Name}) cannot be converted to it: {e.Message}");
        }

        throw new BadCellException($"'{path}' is a {target.Name}, and was given a {value.GetType().Name}.");
    }
}
