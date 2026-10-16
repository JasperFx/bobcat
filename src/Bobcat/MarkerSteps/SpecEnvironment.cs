using System.Runtime.ExceptionServices;
using Bobcat.Engine;

namespace Bobcat;

/// <summary>
/// Whether this process's test environment is still usable (bobcat#539). Once a
/// <see cref="SpecCatastrophicException"/> is thrown anywhere in the process, every scenario that
/// opens afterwards fails at once with a <see cref="SpecEnvironmentLostException"/> naming where the
/// environment was lost, instead of running against an environment that is gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The canonical case is an application that will not start.</b> A Playwright suite starts its
/// app from a fixture before any scenario opens; when it cannot start, every test would otherwise
/// spend its own timeout finding that out. Throwing <see cref="SpecCatastrophicException"/> from the
/// fixture reports the real startup error on the test that met it and fails every later scenario in
/// the process immediately with "environment lost: …".
/// </para>
/// <para>
/// <b>Why the throw itself, and not a verdict an adapter reads.</b> A fixture, an
/// <c>IAsyncLifetime</c> or a host that fails to start fails <i>before</i> any scenario opens, and in
/// several runners the adapter's bracket never runs for those tests at all (an xUnit v3 class
/// fixture fails every test in its class without invoking a before/after attribute). The one place
/// every route passes through is the throw, so the latch is set from
/// <see cref="AppDomain.FirstChanceException"/> — which means throwing one is the declaration, even
/// if something catches it afterwards.
/// </para>
/// <para>
/// <b>Armed by the runner adapters</b> as soon as the runner instantiates their attributes, which is
/// during discovery and so before any fixture runs, and again whenever a scenario opens. A process
/// that never touches an adapter is never armed.
/// </para>
/// <para>
/// Only scenarios that <i>open</i> after the loss are halted: one already running in parallel keeps
/// its own verdict. Out of process the supervisor is what stops a run; this is the in-process half,
/// which is what plain <c>dotnet test</c> has.
/// </para>
/// </remarks>
public static class SpecEnvironment
{
    private static int _armed;
    private static EnvironmentLoss? _lost;

    /// <summary>The first catastrophic failure this process saw, or null while the environment is intact.</summary>
    public static EnvironmentLoss? Lost => Volatile.Read(ref _lost);

    /// <summary>
    /// Start watching the process for a <see cref="SpecCatastrophicException"/>. Idempotent, and
    /// cheap enough to call from every place that might be first.
    /// </summary>
    public static void Arm()
    {
        if (Interlocked.Exchange(ref _armed, 1) == 0)
        {
            AppDomain.CurrentDomain.FirstChanceException += onFirstChance;
        }
    }

    /// <summary>
    /// Throw a <see cref="SpecEnvironmentLostException"/> if the environment is already lost. For a
    /// fixture that would otherwise spend its own timeout re-discovering that the app will not start:
    /// a per-class fixture still runs for every class, before any scenario is opened to be halted.
    /// </summary>
    public static void ThrowIfLost()
    {
        if (Lost is { } loss) throw new SpecEnvironmentLostException(loss);
    }

    /// <summary>
    /// Record a catastrophic failure. The first one wins, because it is the cause and everything
    /// after it is a consequence.
    /// </summary>
    internal static void Declare(SpecCatastrophicException exception, string? spec)
    {
        // A halted scenario's own exception is a consequence, not a second loss.
        if (exception is SpecEnvironmentLostException) return;

        var loss = new EnvironmentLoss(spec, exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        Interlocked.CompareExchange(ref _lost, loss, null);
    }

    private static void onFirstChance(object? sender, FirstChanceExceptionEventArgs e)
    {
        // Runs on every exception thrown in the process, so it does one type test and nothing that
        // could throw: an exception from here would raise this handler again.
        if (e.Exception is not SpecCatastrophicException catastrophic) return;

        try
        {
            // Ambient on the throwing thread, so a throw from inside a scenario names it, and a throw
            // from a fixture or host startup (no scenario open yet) names none.
            Declare(catastrophic, ScenarioRecorder.Current?.Uid);
        }
        catch
        {
            // Never let the watch disturb the exception it is watching.
        }
    }

    /// <summary>Test seam: forget the loss, so one process can prove the halt more than once.</summary>
    internal static void Reset() => Volatile.Write(ref _lost, null);
}

/// <summary>
/// Where and how the environment was lost: the specification that threw (null for setup, a
/// fixture, or host startup), and the catastrophic exception's type and message.
/// </summary>
public sealed record EnvironmentLoss(string? Spec, string ExceptionType, string Message)
{
    /// <summary>
    /// "environment lost in Feature/Scenario: message", or "environment lost: message" when no
    /// scenario was open.
    /// </summary>
    public string Describe()
        => Spec is null ? $"Environment lost: {Message}" : $"Environment lost in {Spec}: {Message}";
}
