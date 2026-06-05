using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Bobcat.Engine.Verification;

/// <summary>
/// The entry point for type-aware cell comparison. Resolves an
/// <see cref="IValueChecker{T}"/> for the value being checked, runs it, and packages the
/// result as a <see cref="CellResult"/> with structured Expected/Actual/Note.
/// <para>Resolution chain (highest precedence first):</para>
/// <list type="number">
///   <item><c>[Comparison(typeof(X))]</c> override via <see cref="CheckOptions.ComparisonType"/></item>
///   <item>a checker registered through <see cref="Register{T}"/></item>
///   <item>a built-in checker for the type</item>
///   <item>the reflection-based fallback (<c>TypeConverter</c> + <c>EqualityComparer&lt;T&gt;.Default</c>)</item>
/// </list>
/// </summary>
public static class CellCheck
{
    private static readonly ConcurrentDictionary<Type, object> _registered = new();
    private static readonly Dictionary<Type, object> _builtIn = BuildBuiltIns();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _forMethods = new();

    private static readonly MethodInfo ForGeneric =
        typeof(CellCheck).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(For) && m.IsGenericMethodDefinition);

    /// <summary>
    /// Register a checker for <typeparamref name="T"/>. Overrides the built-in for that type.
    /// Intended for suite-level customization.
    /// </summary>
    public static void Register<T>(IValueChecker<T> checker) => _registered[typeof(T)] = checker;

    /// <summary>Remove a previously registered checker for <typeparamref name="T"/>.</summary>
    public static void Unregister<T>() => _registered.TryRemove(typeof(T), out _);

    /// <summary>Clear all registered (non built-in) checkers.</summary>
    public static void ClearRegistrations() => _registered.Clear();

    /// <summary>
    /// Compare <paramref name="actual"/> against <paramref name="expectedText"/> and produce a
    /// <see cref="CellResult"/> with structured Expected/Actual/Note.
    /// </summary>
    public static CellResult For<T>(string name, T actual, string expectedText, CheckOptions? options = null, int rowIndex = -1)
    {
        var result = Check(actual, expectedText, options ?? CheckOptions.Default);
        return ToCellResult(name, result, rowIndex);
    }

    /// <summary>
    /// Compare <paramref name="actual"/> against <paramref name="expectedText"/>, returning the
    /// raw <see cref="CheckResult"/> (no <see cref="CellResult"/> packaging).
    /// </summary>
    public static CheckResult Check<T>(T actual, string expectedText, CheckOptions? options = null)
    {
        options ??= CheckOptions.Default;

        // Type-agnostic tokens, unless the expected is an explicitly quoted string literal.
        if (!IsQuoted(expectedText))
        {
            var token = expectedText.Trim();
            if (token == CellTokens.Null) return CheckNull(actual);
            if (token == CellTokens.Empty) return CheckEmpty(actual);
        }

        var checker = Resolve<T>(options);
        return checker.Check(actual, expectedText, options);
    }

    /// <summary>
    /// Runtime-typed entry point for callers that only have a boxed value (e.g. set
    /// verification reading properties via reflection). Dispatches to <see cref="For{T}"/>
    /// using the runtime type of <paramref name="actual"/>.
    /// </summary>
    public static CellResult ForValue(string name, object? actual, string expectedText, CheckOptions? options = null, int rowIndex = -1)
    {
        options ??= CheckOptions.Default;

        if (actual is null)
        {
            var token = IsQuoted(expectedText) ? null : expectedText.Trim();
            var result = token switch
            {
                CellTokens.Null => CheckResult.Match(CellTokens.Null, CellTokens.Null),
                CellTokens.Empty => CheckResult.Match(CellTokens.Empty, CellTokens.Null),
                _ => CheckResult.Mismatch(StripQuotes(expectedText), CellTokens.Null)
            };
            return ToCellResult(name, result, rowIndex);
        }

        var method = _forMethods.GetOrAdd(actual.GetType(), t => ForGeneric.MakeGenericMethod(t));
        return (CellResult)method.Invoke(null, new[] { name, actual, expectedText, options, rowIndex })!;
    }

    private static IValueChecker<T> Resolve<T>(CheckOptions options)
    {
        if (options.ComparisonType != null)
        {
            var instance = Activator.CreateInstance(options.ComparisonType)
                           ?? throw new InvalidOperationException(
                               $"Could not create checker {options.ComparisonType.FullName}");
            if (instance is not IValueChecker<T> typed)
                throw new InvalidOperationException(
                    $"{options.ComparisonType.FullName} does not implement IValueChecker<{typeof(T).Name}>");
            return typed;
        }

        if (_registered.TryGetValue(typeof(T), out var registered))
            return (IValueChecker<T>)registered;

        if (_builtIn.TryGetValue(typeof(T), out var builtIn))
            return (IValueChecker<T>)builtIn;

        if (typeof(T).IsEnum)
        {
            var enumChecker = Activator.CreateInstance(typeof(EnumChecker<>).MakeGenericType(typeof(T)))!;
            return (IValueChecker<T>)enumChecker;
        }

        return new FallbackChecker<T>();
    }

    private static CheckResult CheckNull<T>(T actual)
    {
        return actual is null
            ? CheckResult.Match(CellTokens.Null, CellTokens.Null)
            : CheckResult.Mismatch(CellTokens.Null, CheckFormat.Of(actual));
    }

    private static CheckResult CheckEmpty<T>(T actual)
    {
        bool isEmpty;
        if (actual is null) isEmpty = true;
        else if (actual is string s) isEmpty = s.Length == 0;
        else if (actual is IEnumerable enumerable) isEmpty = !enumerable.GetEnumerator().MoveNext();
        else isEmpty = false;

        return isEmpty
            ? CheckResult.Match(CellTokens.Empty, CellTokens.Empty)
            : CheckResult.Mismatch(CellTokens.Empty, CheckFormat.Of(actual));
    }

    private static CellResult ToCellResult(string name, CheckResult result, int rowIndex)
    {
        var status = result.Outcome switch
        {
            CheckOutcome.Match => ResultStatus.success,
            CheckOutcome.Mismatch => ResultStatus.failed,
            _ => ResultStatus.invalid
        };

        return new CellResult(name, status)
        {
            Expected = result.Expected,
            Actual = result.Actual,
            Note = result.Note,
            RowIndex = rowIndex
        };
    }

    private static bool IsQuoted(string text)
        => text.Length >= 2 && text.StartsWith("\"") && text.EndsWith("\"");

    private static string StripQuotes(string text)
        => IsQuoted(text) ? text.Substring(1, text.Length - 2) : text;

    private static Dictionary<Type, object> BuildBuiltIns() => new()
    {
        [typeof(int)] = new Int32Checker(),
        [typeof(long)] = new Int64Checker(),
        [typeof(double)] = new DoubleChecker(),
        [typeof(float)] = new SingleChecker(),
        [typeof(decimal)] = new DecimalChecker(),
        [typeof(bool)] = new BoolChecker(),
        [typeof(DateTime)] = new DateTimeChecker(),
        [typeof(DateOnly)] = new DateOnlyChecker(),
        [typeof(TimeOnly)] = new TimeOnlyChecker(),
        [typeof(TimeSpan)] = new TimeSpanChecker(),
        [typeof(Guid)] = new GuidChecker(),
        [typeof(string)] = new StringChecker(),
    };
}
