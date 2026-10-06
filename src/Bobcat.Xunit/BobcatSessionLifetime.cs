using Microsoft.Testing.Platform.Builder;
using Microsoft.Testing.Platform.Extensions.TestHost;
using Microsoft.Testing.Platform.TestHost;

namespace Bobcat.Xunit;

/// <summary>
/// Brackets the Bobcat run around a <b>test session</b> rather than around the process (issue
/// #402), so a live server-mode host serving several run requests publishes one run per request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem it closes.</b> A projected suite used to open its run on the first scenario and
/// close it from a <c>ProcessExit</c> handler. In a one-shot <c>dotnet test</c> that is exactly
/// right — one process, one run. In a live process serving two run requests it was wrong three
/// ways at once: one <c>run_started</c>, one <c>RunId</c>, and no <c>run_finished</c> at all, with
/// the second command's scenarios landing on the first command's card. A run with no finish is
/// indistinguishable from a wedged one, which is the failure issue #195 was opened for.
/// </para>
/// <para>
/// <b>A session really is a request, and that was measured.</b> On Microsoft.Testing.Platform
/// 1.9.1 — the version this repository pins — three <c>testing/runTests</c> requests into one live
/// process fired this handler three times, each with its own <see cref="SessionUid"/> and each
/// properly bracketed. A one-shot direct run fired it exactly once. And <b>discovery opens no
/// session</b>, which is what keeps <c>--list-tests</c> from putting an empty card on the board.
/// </para>
/// <para>
/// <b><c>ProcessExit</c> stays, as the backstop.</b> <see cref="MarkerStepRun.CloseRun"/> is
/// idempotent, so when this handler has already closed the bracket the backstop finds nothing to
/// close and only drains the publisher. That matters for any host where this extension is not
/// registered — a TUnit suite, or an xUnit project that has not picked up the props — which then
/// behaves exactly as it did before: one bracket per process.
/// </para>
/// <para>
/// <b>Why the session end does not report counts.</b> It does not have to: the bracket's own
/// pass/fail tallies are kept by <see cref="MarkerStepRun"/> and reset when it closes, so each
/// request's <c>run_finished</c> reports that request's results and not the process's running
/// total.
/// </para>
/// </remarks>
public sealed class BobcatSessionLifetime : ITestSessionLifetimeHandler
{
    public string Uid => nameof(BobcatSessionLifetime);

    public string Version => "1.0.0";

    public string DisplayName => "Bobcat run bracket";

    public string Description =>
        "Opens and closes the Bobcat run bracket around each test session, so a host serving "
        + "several run requests publishes one run per request.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task OnTestSessionStartingAsync(SessionUid sessionUid, CancellationToken cancellationToken)
    {
        MarkerStepRun.OpenRun(XunitScenarioBracket.Mode);
        return Task.CompletedTask;
    }

    public Task OnTestSessionFinishingAsync(SessionUid sessionUid, CancellationToken cancellationToken)
    {
        MarkerStepRun.CloseRun();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Registers <see cref="BobcatSessionLifetime"/> with the test platform.
/// </summary>
/// <remarks>
/// <para>
/// <b>The name and shape are the platform's convention, not a choice.</b>
/// Microsoft.Testing.Platform's MSBuild task generates a <c>SelfRegisteredExtensions</c> class
/// that calls <c>AddExtensions</c> on every type named by a <c>TestingPlatformBuilderHook</c>
/// MSBuild item, and xUnit v3's generated entry point calls that class. So the registration is
/// half C# and half MSBuild: <c>buildTransitive/Bobcat.Xunit.props</c> carries the item for a
/// package consumer.
/// </para>
/// <para>
/// <b>In-repo projects must declare the item themselves.</b> A <c>ProjectReference</c> does not
/// consume another project's build assets — the same rule that makes
/// <c>Bobcat.Mtp.SampleHost</c> set <c>GenerateTestingPlatformEntryPoint</c> by hand.
/// </para>
/// <para>
/// <b>This is the first platform extension Bobcat.Xunit ships, and that was a dependency
/// decision.</b> <c>xunit.v3.extensibility.core</c> depends only on <c>xunit.v3.common</c>, so the
/// package had no Microsoft.Testing.Platform reference until now. Adding it costs a consumer
/// nothing, because a consumer of this package is by definition an xUnit v3 MTP test host and
/// already resolves the platform through <c>xunit.v3</c>. <b>Bobcat.TUnit deliberately does not
/// get the same treatment</b> — see the class comment there.
/// </para>
/// </remarks>
public static class TestingPlatformBuilderHook
{
    public static void AddExtensions(ITestApplicationBuilder builder, string[] arguments)
        => builder.TestHost.AddTestSessionLifetimeHandle(_ => new BobcatSessionLifetime());
}
