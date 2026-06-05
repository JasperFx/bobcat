using System.ComponentModel;
using System.Globalization;

namespace Bobcat.Engine.Verification;

/// <summary>
/// Shared formatting helpers for built-in checkers so actual/expected render consistently.
/// </summary>
internal static class CheckFormat
{
    public static string Of(object? value) => value switch
    {
        null => CellTokens.Null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
}

/// <summary>
/// Default numeric checker shared by integral and floating-point types. Honors
/// <see cref="CheckOptions.Tolerance"/> for approximate comparisons.
/// </summary>
internal abstract class NumericChecker<T> : IValueChecker<T>
{
    protected abstract bool TryParse(string text, out double value);
    protected abstract string Format(T value);
    protected abstract double ToDouble(T value);

    public CheckResult Check(T actual, string expectedText, CheckOptions options)
    {
        var text = expectedText.Trim();
        var actualText = Format(actual);

        if (!TryParse(text, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid {typeof(T).Name}");

        var actualValue = ToDouble(actual);
        // Echo the author's expected text (preserves scale like "9.00") rather than the
        // reduced double representation.
        var expectedDisplay = text;

        if (options.Tolerance is { } tol)
        {
            var within = Math.Abs(actualValue - expected) <= tol;
            var note = $"±{tol.ToString(CultureInfo.InvariantCulture)}";
            return within
                ? CheckResult.Match(expectedDisplay, actualText, note)
                : CheckResult.Mismatch(expectedDisplay, actualText, note);
        }

        return actualValue == expected
            ? CheckResult.Match(expectedDisplay, actualText)
            : CheckResult.Mismatch(expectedDisplay, actualText);
    }
}

internal sealed class Int32Checker : NumericChecker<int>
{
    protected override bool TryParse(string text, out double value)
    {
        var ok = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i);
        value = i;
        return ok;
    }

    protected override string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
    protected override double ToDouble(int value) => value;
}

internal sealed class Int64Checker : NumericChecker<long>
{
    protected override bool TryParse(string text, out double value)
    {
        var ok = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l);
        value = l;
        return ok;
    }

    protected override string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    protected override double ToDouble(long value) => value;
}

internal sealed class DoubleChecker : NumericChecker<double>
{
    protected override bool TryParse(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    protected override string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    protected override double ToDouble(double value) => value;
}

internal sealed class SingleChecker : NumericChecker<float>
{
    protected override bool TryParse(string text, out double value)
    {
        var ok = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f);
        value = f;
        return ok;
    }

    protected override string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
    protected override double ToDouble(float value) => value;
}

internal sealed class DecimalChecker : NumericChecker<decimal>
{
    protected override bool TryParse(string text, out double value)
    {
        var ok = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d);
        value = ok ? (double)d : 0d;
        return ok;
    }

    protected override string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    protected override double ToDouble(decimal value) => (double)value;
}

internal sealed class BoolChecker : IValueChecker<bool>
{
    public CheckResult Check(bool actual, string expectedText, CheckOptions options)
    {
        var actualText = actual ? "true" : "false";
        var text = expectedText.Trim().ToLowerInvariant();

        bool? expected = text switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => null
        };

        if (expected is null)
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid bool");

        var expectedText2 = expected.Value ? "true" : "false";
        return expected.Value == actual
            ? CheckResult.Match(expectedText2, actualText)
            : CheckResult.Mismatch(expectedText2, actualText);
    }
}

internal sealed class DateTimeChecker : IValueChecker<DateTime>
{
    public CheckResult Check(DateTime actual, string expectedText, CheckOptions options)
    {
        var actualText = CheckFormat.Of(actual);
        if (!DateTime.TryParse(expectedText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid DateTime");

        return expected == actual
            ? CheckResult.Match(CheckFormat.Of(expected), actualText)
            : CheckResult.Mismatch(CheckFormat.Of(expected), actualText);
    }
}

internal sealed class DateOnlyChecker : IValueChecker<DateOnly>
{
    public CheckResult Check(DateOnly actual, string expectedText, CheckOptions options)
    {
        var actualText = CheckFormat.Of(actual);
        if (!DateOnly.TryParse(expectedText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid DateOnly");

        return expected == actual
            ? CheckResult.Match(CheckFormat.Of(expected), actualText)
            : CheckResult.Mismatch(CheckFormat.Of(expected), actualText);
    }
}

internal sealed class TimeOnlyChecker : IValueChecker<TimeOnly>
{
    public CheckResult Check(TimeOnly actual, string expectedText, CheckOptions options)
    {
        var actualText = CheckFormat.Of(actual);
        if (!TimeOnly.TryParse(expectedText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid TimeOnly");

        return expected == actual
            ? CheckResult.Match(CheckFormat.Of(expected), actualText)
            : CheckResult.Mismatch(CheckFormat.Of(expected), actualText);
    }
}

internal sealed class TimeSpanChecker : IValueChecker<TimeSpan>
{
    public CheckResult Check(TimeSpan actual, string expectedText, CheckOptions options)
    {
        var actualText = CheckFormat.Of(actual);
        if (!FriendlyTimeSpanParser.TryParse(expectedText, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid duration");

        return expected == actual
            ? CheckResult.Match(CheckFormat.Of(expected), actualText)
            : CheckResult.Mismatch(CheckFormat.Of(expected), actualText);
    }
}

internal sealed class GuidChecker : IValueChecker<Guid>
{
    public CheckResult Check(Guid actual, string expectedText, CheckOptions options)
    {
        var actualText = actual.ToString();
        if (!Guid.TryParse(expectedText.Trim(), out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid Guid");

        return expected == actual
            ? CheckResult.Match(expected.ToString(), actualText)
            : CheckResult.Mismatch(expected.ToString(), actualText);
    }
}

/// <summary>
/// Enum checker — matches by name (case-insensitive) or by numeric value.
/// </summary>
internal sealed class EnumChecker<T> : IValueChecker<T> where T : struct, Enum
{
    public CheckResult Check(T actual, string expectedText, CheckOptions options)
    {
        var actualText = actual.ToString();
        var text = expectedText.Trim();

        if (!Enum.TryParse<T>(text, ignoreCase: true, out var expected) || !IsDefinedOrNumeric(text, expected))
            return CheckResult.Invalid(expectedText, actualText, $"'{expectedText}' is not a valid {typeof(T).Name}");

        return EqualityComparer<T>.Default.Equals(expected, actual)
            ? CheckResult.Match(expected.ToString(), actualText)
            : CheckResult.Mismatch(expected.ToString(), actualText);
    }

    private static bool IsDefinedOrNumeric(string text, T parsed)
    {
        // Enum.TryParse accepts arbitrary numeric strings; treat a bare number as valid
        // (numeric match), but reject undefined names.
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
        return Enum.IsDefined(typeof(T), parsed);
    }
}

/// <summary>
/// Last-resort checker: convert expected text to <typeparamref name="T"/> via a
/// <see cref="TypeConverter"/> / <see cref="Convert.ChangeType(object, Type)"/>, then compare
/// with <see cref="EqualityComparer{T}.Default"/>.
/// </summary>
internal sealed class FallbackChecker<T> : IValueChecker<T>
{
    public CheckResult Check(T actual, string expectedText, CheckOptions options)
    {
        var actualText = CheckFormat.Of(actual);
        var text = options.Trim ? expectedText.Trim() : expectedText;

        if (!TryConvert(text, out var expected))
            return CheckResult.Invalid(expectedText, actualText, $"could not convert '{expectedText}' to {typeof(T).Name}");

        return EqualityComparer<T>.Default.Equals(actual, expected!)
            ? CheckResult.Match(CheckFormat.Of(expected), actualText)
            : CheckResult.Mismatch(CheckFormat.Of(expected), actualText);
    }

    private static bool TryConvert(string text, out T value)
    {
        value = default!;
        try
        {
            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter.CanConvertFrom(typeof(string)))
            {
                var converted = converter.ConvertFromInvariantString(text);
                if (converted is null) return false;
                value = (T)converted;
                return true;
            }

            value = (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// String checker — case-sensitive by default. Trims both sides unless the expected text
/// is wrapped in double quotes, in which case the inner value is compared verbatim.
/// </summary>
internal sealed class StringChecker : IValueChecker<string>
{
    public CheckResult Check(string actual, string expectedText, CheckOptions options)
    {
        actual ??= "";

        string expected;
        string actualToCompare;

        if (expectedText.Length >= 2 && expectedText.StartsWith("\"") && expectedText.EndsWith("\""))
        {
            // Quoted — preserve whitespace exactly on both sides.
            expected = expectedText.Substring(1, expectedText.Length - 2);
            actualToCompare = actual;
        }
        else if (options.Trim)
        {
            expected = expectedText.Trim();
            actualToCompare = actual.Trim();
        }
        else
        {
            expected = expectedText;
            actualToCompare = actual;
        }

        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(actualToCompare, expected, comparison)
            ? CheckResult.Match(expected, actual)
            : CheckResult.Mismatch(expected, actual);
    }
}
