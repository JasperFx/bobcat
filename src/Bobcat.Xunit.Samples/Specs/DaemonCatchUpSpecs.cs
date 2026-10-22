using Bobcat.Xunit.Samples.Grammars;

namespace Bobcat.Xunit.Samples.Specs;

/// <summary>
/// The <c>docs/marker-steps.md</c> marker-comment example, in the shape of Marten's daemon tests:
/// <c>[BobcatFeature]</c> on an existing class, recorded by the assembly-level hook, with comments for
/// steps and one ordinary comment that stays a comment. Green.
/// </summary>
// #region marker-comments
[BobcatFeature("Async daemon")]
public class when_the_daemon_catches_up : DaemonContext
{
    [Fact]
    public async Task the_projection_catches_up()
    {
        // Given the events are published
        await PublishEvents();

        // the daemon polls on its own schedule, which is why the wait below exists
        await WaitForNonStaleAsync();

        // When the projection daemon is running
        using var daemon = await StartDaemon();

        // Then every expected aggregate matches
        await CheckAllExpectedAggregatesAgainstActuals();
    }
}
// #endregion marker-comments

/// <summary>
/// Marker comments and step helpers together (issue #305): the helpers called under a comment render
/// underneath it, and a comment with no helper under it stays <c>declared</c>. And a marker wrapped onto
/// a second line (bobcat#687). Green.
/// </summary>
[BobcatFeature("Async daemon")]
public class DaemonStepSpecs : DaemonContext
{
    // #region nested-steps
    [Fact]
    public async Task the_projection_catches_up_on_three_threads()
    {
        // Given the events are published
        await PublishMultiThreaded(3);
        await FlushEvents();

        // When the projection daemon is running
        using var daemon = await StartDaemon();

        // Then every expected aggregate matches
        await CheckAllExpectedAggregatesAgainstActuals();
    }
    // #endregion nested-steps

    [Fact]
    public async Task a_failing_event_stops_the_projection_at_the_committed_mark()
    {
        PoisonEvent = 15;

        // Given the events are published
        await PublishEvents();

        // When the projection daemon is running
        using var daemon = await StartDaemon();

        // #region wrapped-marker
        // Then every event at or below the committed mark is in a document already; nothing past the
        // failing event is in one
        await CheckDocuments();
        // #endregion wrapped-marker
    }
}
