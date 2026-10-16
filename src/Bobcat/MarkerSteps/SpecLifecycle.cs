using System.Reflection;

namespace Bobcat;

/// <summary>
/// Where a specification stands (bobcat#545, Storyteller's Acceptance/Regression split):
/// <see cref="Regression"/> is a promise the build keeps, <see cref="Acceptance"/> is a promise not
/// yet kept.
/// </summary>
public enum Lifecycle
{
    /// <summary>
    /// The default. A failure is a failure: the test platform reports it failed and the build goes red.
    /// </summary>
    Regression,

    /// <summary>
    /// A specification written before the behaviour it describes is finished. It runs every time and is
    /// never retried; a failure is recorded in full by Bobcat as <c>acceptance: failing</c>, while the
    /// test platform reports the test <b>skipped</b>, so the build stays green. A passing one is
    /// ready to promote to <see cref="Regression"/>. <c>BOBCAT_LIFECYCLE=strict</c> makes its failures
    /// real failures.
    /// </summary>
    Acceptance
}

/// <summary>
/// Implemented by a runner adapter's attribute that can declare a specification's
/// <see cref="Lifecycle"/> — <c>Bobcat.Xunit</c>'s <c>[BobcatSpec]</c>, and the <c>[BobcatScenario]</c>
/// of the TUnit, NUnit and MSTest adapters — so core reads it without referencing a runner, the way
/// <see cref="IExplicitScenarioName"/> works.
/// </summary>
/// <remarks>
/// Implemented explicitly beside a plain <c>Lifecycle</c> property (an attribute argument cannot be
/// nullable), so "never stated" stays distinguishable from "stated Regression": a method attribute
/// that says nothing must not override its class's.
/// </remarks>
public interface IDeclaresLifecycle
{
    /// <summary>The lifecycle stated, or null when the attribute did not state one.</summary>
    Lifecycle? Lifecycle { get; }
}

/// <summary>
/// The rules of the Acceptance lifecycle (bobcat#545) and of strict mode (C13), in one place, so the
/// four adapters and every rendering read the same answer.
/// </summary>
public static class SpecLifecycle
{
    /// <summary>
    /// The environment variable that turns strict mode on: <c>BOBCAT_LIFECYCLE=strict</c>.
    /// </summary>
    public const string EnvironmentVariable = "BOBCAT_LIFECYCLE";

    /// <summary>The value of <see cref="EnvironmentVariable"/> that means strict.</summary>
    public const string StrictValue = "strict";

    /// <summary>What every skipped failing Acceptance spec's reason starts with.</summary>
    public const string SkipReasonPrefix = "acceptance spec, not yet passing: ";

    /// <summary>The verdict word for a failing Acceptance spec on every Bobcat surface.</summary>
    public const string FailingVerdict = "acceptance: failing";

    /// <summary>What a passing Acceptance spec says about itself.</summary>
    public const string ReadyToPromote = "ready to promote to Regression";

    /// <summary>What a pending specification fails with in strict mode, before its identity.</summary>
    public const string PendingInStrictModePrefix = "pending specification in strict mode: ";

    /// <summary>
    /// Whether this run is strict: nothing unfinished passes. A failing Acceptance spec is a real
    /// failure, and a <c>Pending = true</c> spec fails instead of being skipped (C13). Read on every
    /// call, so it is whatever the environment says now.
    /// </summary>
    public static bool Strict
        => string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim(), StrictValue,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lifecycle a test method declares: the method's own attribute, else its class's, else
    /// <see cref="Lifecycle.Regression"/>. An attribute stating <see cref="Lifecycle.Regression"/> on the
    /// method beats an Acceptance class, which is how one spec in an Acceptance class is promoted first.
    /// </summary>
    public static Lifecycle Of(MethodInfo? method)
    {
        if (method is null) return Lifecycle.Regression;
        return declaredOn(method) ?? declaredOn(method.DeclaringType) ?? Lifecycle.Regression;
    }

    /// <summary>
    /// The lifecycle of a test a runner names by type and method name (TUnit hands out no
    /// <see cref="MethodInfo"/>). Any overload of that name stating one counts.
    /// </summary>
    public static Lifecycle Of(Type? declaringType, string methodName)
    {
        if (declaringType is null) return Lifecycle.Regression;

        var onMethod = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(x => x.Name == methodName)
            .Select(x => declaredOn(x))
            .FirstOrDefault(x => x is not null);

        return onMethod ?? declaredOn(declaringType) ?? Lifecycle.Regression;
    }

    /// <summary>
    /// The skip reason a failing Acceptance spec carries to its test platform:
    /// <c>acceptance spec, not yet passing: &lt;first failure&gt;</c>.
    /// </summary>
    public static string SkipReasonFor(string? firstFailure)
        => SkipReasonPrefix + (string.IsNullOrWhiteSpace(firstFailure) ? "Failed" : firstFailure.Trim());

    /// <summary>
    /// The first line of a failure description that says something — the gathered-failures heading
    /// ("1 specification step failed:") is skipped for the step under it.
    /// </summary>
    public static string FirstFailure(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "Failed";

        var lines = description
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        var first = lines.FirstOrDefault(x => !isHeading(x)) ?? lines[0];
        return first;
    }

    /// <summary>The message a pending specification fails with under <see cref="Strict"/>.</summary>
    public static string PendingInStrictMode(string identity) => PendingInStrictModePrefix + identity;

    private static bool isHeading(string line)
        => line.EndsWith(':')
           && (line.Contains("specification step", StringComparison.Ordinal)
               || line.Contains("SpecAssert wrong", StringComparison.Ordinal));

    private static Lifecycle? declaredOn(MemberInfo? member)
        => member?.GetCustomAttributes(inherit: true)
            .OfType<IDeclaresLifecycle>()
            .Select(x => x.Lifecycle)
            .FirstOrDefault(x => x is not null);
}
