using System.Collections.Concurrent;
using System.ComponentModel;
using Xunit.Sdk;
using Xunit.v3;

namespace Bobcat.Xunit;

/// <summary>
/// Discovers a <see cref="BobcatSpecAttribute"/> test: an ordinary <see cref="XunitTestCase"/>, except
/// that an <see cref="Lifecycle.Acceptance"/> spec becomes an <see cref="AcceptanceTestCase"/>
/// (bobcat#545).
/// </summary>
public class BobcatSpecDiscoverer : FactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions, IXunitTestMethod testMethod, IFactAttribute factAttribute)
    {
        var testCase = base.CreateTestCase(discoveryOptions, testMethod, factAttribute);

        return factAttribute is BobcatSpecAttribute { Lifecycle: Lifecycle.Acceptance } && testCase is XunitTestCase plain
            ? new AcceptanceTestCase(plain)
            : testCase;
    }
}

/// <summary>
/// The xUnit test case of an <see cref="Lifecycle.Acceptance"/> spec (bobcat#545): it runs exactly as
/// any other, and when Bobcat closed its scenario as <c>acceptance: failing</c> it reports the test
/// <b>skipped</b> instead of failed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just throw a skip from the after-test hook.</b> That works when the body finished and
/// only gathered <c>SpecAssert</c> wrongs failed it, and the bracket does exactly that. When the body
/// <em>threw</em>, xUnit already holds that exception, a second one from the hook makes an
/// <see cref="AggregateException"/>, and an aggregate is never read as a skip — measured against
/// xunit.v3 4.0.2's <c>TestRunner</c>. So the case runs itself (<see cref="ISelfExecutingXunitTestCase"/>)
/// and turns the <c>TestFailed</c> message for that one test into a <c>TestSkipped</c> on the way to
/// the runner, with the counts to match.
/// </para>
/// <para>
/// <b>The decision is Bobcat's, not this class's.</b> It only rewrites a test the bracket registered
/// through <see cref="SkipAsAcceptance"/>, so a strict run (<c>BOBCAT_LIFECYCLE=strict</c>), a passing
/// spec, or a failure outside the scenario reaches the runner untouched.
/// </para>
/// </remarks>
public class AcceptanceTestCase : XunitTestCase, ISelfExecutingXunitTestCase
{
    /// <summary>Tests the bracket closed as <c>acceptance: failing</c>, by xUnit test id, with their skip reason.</summary>
    private static readonly ConcurrentDictionary<string, string> _skips = new();

    /// <summary>Called by the deserializer; never call it from code.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public AcceptanceTestCase()
    {
    }

    public AcceptanceTestCase(XunitTestCase plain)
        : base(
            plain.TestMethod, plain.TestCaseDisplayName, plain.UniqueID, plain.Explicit, plain.TestLabel,
            plain.DisableParallelization, plain.SkipExceptions, plain.SkipReason, plain.SkipType, plain.SkipUnless,
            plain.SkipWhen, plain.Traits, plain.TestMethodArguments, plain.SourceFilePath, plain.SourceLineNumber,
            plain.Timeout)
    {
    }

    /// <summary>
    /// Report <paramref name="testUniqueId"/> skipped with <paramref name="reason"/> when it fails. Called
    /// by the bracket once Bobcat has closed the scenario as <c>acceptance: failing</c>.
    /// </summary>
    internal static void SkipAsAcceptance(string testUniqueId, string reason) => _skips[testUniqueId] = reason;

    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption, IMessageBus messageBus, object?[] constructorArguments,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, ParallelMode parallelMode,
        ExecutionScheduler scheduler, FixtureMappingManager methodFixtureMappings)
    {
        var bus = new AcceptanceMessageBus(messageBus);

        var summary = await XunitRunnerHelper.RunXunitTestCase(
            this, bus, cancellationTokenSource, parallelMode, scheduler, aggregator, explicitOption,
            constructorArguments, methodFixtureMappings);

        summary.Failed -= bus.Converted;
        summary.Skipped += bus.Converted;
        return summary;
    }

    /// <summary>
    /// Passes every message through, except a registered test's <c>TestFailed</c> (sent as
    /// <c>TestSkipped</c>) and its case's <c>TestCaseFinished</c> (sent with the counts moved).
    /// </summary>
    private sealed class AcceptanceMessageBus(IMessageBus inner) : IMessageBus
    {
        public int Converted { get; private set; }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestFailed failed when _skips.TryRemove(failed.TestUniqueID, out var reason):
                    Converted++;
                    return inner.QueueMessage(new TestSkipped
                    {
                        AssemblyUniqueID = failed.AssemblyUniqueID,
                        TestCollectionUniqueID = failed.TestCollectionUniqueID,
                        TestClassUniqueID = failed.TestClassUniqueID,
                        TestMethodUniqueID = failed.TestMethodUniqueID,
                        TestCaseUniqueID = failed.TestCaseUniqueID,
                        TestUniqueID = failed.TestUniqueID,
                        ExecutionTime = failed.ExecutionTime,
                        FinishTime = failed.FinishTime,
                        Output = failed.Output,
                        Warnings = failed.Warnings,
                        Reason = reason
                    });

                case ITestCaseFinished finished when Converted > 0:
                    return inner.QueueMessage(new TestCaseFinished
                    {
                        AssemblyUniqueID = finished.AssemblyUniqueID,
                        TestCollectionUniqueID = finished.TestCollectionUniqueID,
                        TestClassUniqueID = finished.TestClassUniqueID,
                        TestMethodUniqueID = finished.TestMethodUniqueID,
                        TestCaseUniqueID = finished.TestCaseUniqueID,
                        ExecutionTime = finished.ExecutionTime,
                        FinishTime = finished.FinishTime,
                        TestsTotal = finished.TestsTotal,
                        TestsNotRun = finished.TestsNotRun,
                        TestsFailed = Math.Max(0, finished.TestsFailed - Converted),
                        TestsSkipped = finished.TestsSkipped + Converted
                    });

                default:
                    return inner.QueueMessage(message);
            }
        }

        // The inner bus belongs to the runner that handed it over.
        public void Dispose()
        {
        }
    }
}
