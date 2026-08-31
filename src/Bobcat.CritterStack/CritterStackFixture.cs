using Bobcat.Engine;
using Bobcat.Wolverine;
using JasperFx.Events;
using Wolverine.Tracking;

namespace Bobcat.CritterStack;

/// <summary>
/// The base fixture for Critter Stack event-sourcing specs: a <see cref="Fixture"/> that already
/// carries the typed event-sourcing steps <b>and</b> the shipped Gherkin grammar that reads as an
/// Event Modeling slice. Derive from it — <c>public class WithdrawFunds : CritterStackFixture</c> —
/// and every <c>Given events for …</c> / <c>When … is received</c> / <c>Then … is emitted</c> step
/// is bound with no further code, because the generator discovers steps declared on base classes
/// (issue #104). That is the canonical route; see <see cref="CritterStackGrammars"/> for the mix-in
/// route when a fixture cannot own the base type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-agnostic.</b> Every operation reaches the event store through the <c>JasperFx.Events</c>
/// abstractions resolved from the registered <see cref="Runtime.IHostResource"/> — Marten, Polecat
/// and Fisher alike — so this fixture (and a spec deriving from it) has no reference to any store.
/// </para>
/// <para>
/// <b>When-vs-Then failure semantics mirror JasperFx's <c>ProjectionScenario</c>.</b> The arrange
/// steps (<see cref="GivenEvents{T}"/>) commit through a session; a failure there throws and stops
/// the scenario, because every later step would run against a state nobody intended. The act step
/// (<see cref="WhenCommand{T}"/>) <i>captures</i> the command's outcome — a success, or a domain /
/// validation failure — into <see cref="LastError"/> so a <c>Then</c> can assert on it; that is what
/// lets <c>Then validation fails with …</c> work. The assertion steps throw
/// <see cref="SpecAssertionException"/> on a mismatch, which Bobcat records as an accumulating
/// assertion failure rather than a scenario-aborting error.
/// </para>
/// <para>
/// <b>Two refusal styles, two steps (issue #168).</b> <c>Then validation fails with …</c>
/// describes a refusal that <i>throws</i> — a guard raising an exception the dispatch captures
/// into <see cref="LastError"/>. Wolverine's recommended messaging railway refuses <i>without</i>
/// throwing (<c>HandlerContinuation.Stop</c> from a <c>Before</c>/<c>Load</c>), and that is what
/// <c>Then the command is refused</c> describes: dispatched, nothing thrown, nothing appended.
/// The clean form is deliberately reason-less, because a clean stop's reason exists only in
/// Wolverine's own log stream, never as data a spec could assert on.
/// </para>
/// <para>
/// Override <see cref="HostResource"/> / <see cref="StoreName"/> when a suite registers more than
/// one host or a host registers more than one store; both default to "the only one".
/// </para>
/// </remarks>
public abstract class CritterStackFixture : Fixture
{
    /// <summary>Names the <see cref="Runtime.IHostResource"/> when a suite registers several. Null = the only one.</summary>
    protected virtual string? HostResource => null;

    /// <summary>Names the store (by <see cref="IEventStore.Identity"/>) when a host registers several. Null = the only one.</summary>
    protected virtual string? StoreName => null;

    /// <summary>How long a projection wait may run before <see cref="ThenDocument{T}"/> gives up.</summary>
    protected virtual TimeSpan ProjectionTimeout => TimeSpan.FromSeconds(20);

    /// <summary>
    /// The stream the scenario is arranging and acting on, set by a Given — when the stream is
    /// Guid-identified. <see cref="Guid.Empty"/> when the scenario arranged a string-keyed stream
    /// (see <see cref="StreamKey"/>) or no stream at all.
    /// </summary>
    protected Guid StreamId { get; private set; }

    /// <summary>
    /// The stream key the scenario is arranging and acting on, for stores using string stream
    /// identity (bobcat#177 — Stoat's <c>{plan}/{nodeId}</c> claims, CritterWatch's service
    /// streams). Null when the scenario arranged a Guid-identified stream. The grammar step
    /// <c>Given no events for {aggregate} "{id}"</c> decides which: an id that parses as a Guid
    /// is one, anything else is a stream key verbatim.
    /// </summary>
    protected string? StreamKey { get; private set; }

    /// <summary>The current stream identity — the key when string-keyed, else the Guid — boxed for
    /// the polymorphic load/fetch paths. Null when no Given has established a stream.</summary>
    private object? streamIdentity => StreamKey ?? (StreamId == Guid.Empty ? null : (object)StreamId);

    /// <summary>The aggregate type the current stream belongs to, from the last Given.</summary>
    protected Type? AggregateType { get; private set; }

    /// <summary>The events the last <see cref="WhenCommand{T}"/> appended, or empty.</summary>
    protected IReadOnlyList<IEvent> LastEvents { get; private set; } = [];

    /// <summary>The tracked Wolverine session of the last command, or null when it failed to run.</summary>
    protected ITrackedSession? LastSession { get; private set; }

    /// <summary>The exception the last command raised, or null when it succeeded — the subject of <see cref="ThenValidationFails"/>.</summary>
    protected Exception? LastError { get; private set; }

    private IStepContext Ctx => Context ?? throw new InvalidOperationException(
        "No IStepContext is set on the fixture — a CritterStack step ran outside a scenario.");

    /// <summary>Resets the per-scenario state. Runs base-first, before any derived <c>BeforeEach</c>.</summary>
    public void BeforeEach()
    {
        StreamId = Guid.Empty;
        StreamKey = null;
        AggregateType = null;
        LastEvents = [];
        LastSession = null;
        LastError = null;
    }

    // ---- typed steps (shared with the code-first API, issue #105) -----------------------------

    /// <summary>
    /// Arrange: the stream <paramref name="id"/> (an <typeparamref name="T"/> aggregate) starts with
    /// exactly these events. Establishes the stream a later <see cref="WhenCommand{T}"/> runs against.
    /// An empty <paramref name="events"/> just records the id — the "no events yet" starting point.
    /// </summary>
    public Task GivenEvents<T>(Guid id, params object[] events) where T : class
    {
        StreamId = id;
        StreamKey = null;
        return givenEventsCore<T>(id, events);
    }

    /// <summary>
    /// The string-keyed twin of <see cref="GivenEvents{T}(Guid, object[])"/>, for stores using
    /// string stream identity (bobcat#177).
    /// </summary>
    public Task GivenEvents<T>(string key, params object[] events) where T : class
    {
        StreamKey = key;
        StreamId = Guid.Empty;
        return givenEventsCore<T>(key, events);
    }

    private async Task givenEventsCore<T>(object identity, object[] events) where T : class
    {
        AggregateType = typeof(T);
        Ctx.RecordTouchedType(typeof(T));
        if (events.Length > 0)
        {
            await EventStoreAuthoring.AppendAsync(Ctx.EventStore(HostResource, StoreName), typeof(T), identity, events, Ctx.Cancellation);
            recordTouched(events);
        }
    }

    /// <summary>The stream <paramref name="id"/> (an <typeparamref name="T"/> aggregate) has no events yet.</summary>
    public Task GivenNoEvents<T>(Guid id) where T : class => GivenEvents<T>(id);

    /// <inheritdoc cref="GivenNoEvents{T}(Guid)"/>
    public Task GivenNoEvents<T>(string key) where T : class => GivenEvents<T>(key);

    /// <summary>
    /// Act: send <paramref name="command"/> through Wolverine, wait for the tracked session to settle,
    /// and capture the events it appended to the current stream and the rebuilt <typeparamref name="T"/>
    /// aggregate. A domain / validation failure is captured (see class remarks), not thrown; the return
    /// is null in that case and <see cref="LastError"/> is set.
    /// </summary>
    public async Task<AggregateExecution<T>?> WhenCommand<T>(object command) where T : class
    {
        await executeCommandCore(command);
        if (LastError != null) return null;

        var aggregate = StreamKey is { } key
            ? await Ctx.AggregateEventStreamAsync<T>(key, HostResource, StoreName)
            : await Ctx.AggregateEventStreamAsync<T>(StreamId, HostResource, StoreName);
        return new AggregateExecution<T>(LastSession!, LastEvents, aggregate);
    }

    /// <summary>Assert the current stream's last command emitted exactly <paramref name="expected"/> (by value).</summary>
    public void ThenEvents(params object[] expected)
    {
        if (LastError != null)
            throw new SpecAssertionException(
                $"Expected {expected.Length} event(s), but the command failed: {LastError.Message}");

        var actual = LastEvents.Select(e => e.Data).ToList();

        if (actual.Count != expected.Length || actual.Where((t, i) => !Equals(t, expected[i])).Any())
            throw new SpecAssertionException(
                $"Emitted events did not match.\n  expected: {describe(expected)}\n  actual:   {describe(actual)}");
    }

    /// <summary>Assert the current stream's last command emitted no events.</summary>
    public void ThenNoEvents()
    {
        if (LastEvents.Count > 0)
            throw new SpecAssertionException(
                $"Expected no events, but {LastEvents.Count} were emitted: {describe(LastEvents.Select(e => e.Data))}");
    }

    /// <summary>
    /// Assert the last command failed and its message contains <paramref name="expected"/> — the
    /// validation / business-rule rejection an Event Modeling slice's sad path specifies.
    /// </summary>
    public void ThenValidationFails(string expected)
    {
        if (LastError == null)
            throw new SpecAssertionException(
                $"Expected the command to fail with a message containing \"{expected}\", but it succeeded.");

        var messages = flatten(LastError).ToList();
        if (!messages.Any(m => m.Contains(expected, StringComparison.OrdinalIgnoreCase)))
            throw new SpecAssertionException(
                $"Expected a failure message containing \"{expected}\", but got: {string.Join(" | ", messages)}");
    }

    /// <summary>
    /// Assert the last command was cleanly refused — Wolverine's non-throwing railway, a
    /// <c>Before</c>/<c>Load</c> returning <c>HandlerContinuation.Stop</c> (issue #168): the
    /// command was dispatched, nothing threw, and no events were appended to the current stream.
    /// The counterpart of <see cref="ThenValidationFails"/>, which describes a refusal that
    /// <i>throws</i> — a handler written the recommended non-throwing way can never satisfy that
    /// step, and this one is how its sad path is specified.
    /// </summary>
    /// <remarks>
    /// Deliberately reason-less: a clean stop carries no reason anywhere Wolverine surfaces —
    /// the validation middleware only ever logs it — so a reason clause here would assert on
    /// nothing. A refusal that also notifies (a cascaded rejection message) composes with
    /// <see cref="ThenMessagesSent{T}"/>.
    /// </remarks>
    public void ThenCommandRefused()
    {
        if (LastSession == null)
            throw new SpecAssertionException(
                LastError != null
                    ? $"Expected the command to be cleanly refused, but it threw: {LastError.Message}. " +
                      "A throwing refusal is what 'Then validation fails with …' describes."
                    : "Expected the command to be refused, but no command has run.");

        if (LastEvents.Count > 0)
            throw new SpecAssertionException(
                $"Expected the command to be refused, but it emitted {LastEvents.Count} event(s): " +
                describe(LastEvents.Select(e => e.Data)));
    }

    /// <summary>
    /// Assert the read-model document of type <typeparamref name="T"/> for the current stream exists
    /// and satisfies <paramref name="assert"/>, after waiting for async projections to catch up.
    /// Delegates the load to the store-agnostic authoring helper (<c>LoadAsync&lt;T&gt;</c>), so it reads
    /// the same against Marten, Polecat or Fisher.
    /// </summary>
    public Task ThenDocument<T>(Action<T> assert) where T : class => ThenDocument(streamIdentity ?? StreamId, assert);

    /// <inheritdoc cref="ThenDocument{T}(System.Action{T})"/>
    public async Task ThenDocument<T>(object id, Action<T> assert) where T : class
    {
        await Ctx.WaitForNonStaleProjectionsAsync(ProjectionTimeout, HostResource, StoreName);

        var document = await EventStoreAuthoring.LoadDocumentAsync<T>(Ctx.EventStore(HostResource, StoreName), id, Ctx.Cancellation);
        if (document == null)
            throw new SpecAssertionException(
                $"Expected a {typeof(T).Name} read-model document with id '{id}', but none exists.");

        Ctx.RecordTouchedType(typeof(T));
        assert(document);
    }

    /// <summary>Assert the last command sent a message of type <typeparamref name="T"/> (at least one).</summary>
    public void ThenMessagesSent<T>(int count = 1)
    {
        if (LastSession == null)
            throw new SpecAssertionException(
                $"Expected {count} message(s) of type {typeof(T).Name} to be sent, but no command has run (or it failed).");

        var sent = LastSession.Sent.MessagesOf<T>().Count();
        if (sent < count)
            throw new SpecAssertionException(
                $"Expected at least {count} message(s) of type {typeof(T).Name} to be sent, but {sent} were.");
    }

    // ---- shipped Gherkin grammar --------------------------------------------------------------
    // The slice-declaring vocabulary. {aggregate}/{command}/{event}/{readmodel}/{message} are type
    // captures the generator resolves to typeof(...) against the consuming compilation (BOBCAT011 /
    // BOBCAT012 when a name is unknown or ambiguous). Each delegates to a typed step above.

    [Given("no events for {aggregate} {string}")]
    public void GivenNoEventsFor(Type aggregate, string id)
    {
        // An id that parses as a Guid is one; anything else is a string stream key verbatim
        // (bobcat#177 — Stoat's "{plan}/{nodeId}" claims, CritterWatch's service streams).
        if (Guid.TryParse(id, out var guid))
        {
            StreamId = guid;
            StreamKey = null;
        }
        else
        {
            StreamKey = id;
            StreamId = Guid.Empty;
        }

        AggregateType = aggregate;
        Ctx.RecordTouchedType(aggregate);
    }

    [Given("events for {aggregate}")]
    public async Task GivenEventsFor(Type aggregate, StepTable events)
    {
        AggregateType = aggregate;
        if (streamIdentity is not { } identity)
            throw new SpecCriticalException(
                "'Given events for …' needs the stream id — precede it with 'Given no events for <aggregate> \"<id>\"' " +
                "(or a step that sets the id).");

        var built = buildEvents(aggregate, events);
        await EventStoreAuthoring.AppendAsync(Ctx.EventStore(HostResource, StoreName), aggregate, identity, built, Ctx.Cancellation);
        Ctx.RecordTouchedType(aggregate);
        recordTouched(built);
    }

    [When("{command} is received")]
    public Task WhenCommandIsReceived(Type command, StepTable fields)
    {
        if (fields.Rows.Count != 1)
            throw new SpecCriticalException(
                $"'When {command.Name} is received' expects exactly one table row of command fields, but got {fields.Rows.Count}.");

        var message = RecordBuilding.Build(command, fields.AsDictionaries()[0]);
        return executeCommandCore(message);
    }

    [Then("{event} is emitted")]
    public void ThenEventIsEmitted(Type @event, StepTable? fields)
    {
        if (LastError != null)
            throw new SpecAssertionException(
                $"Expected a {@event.Name} event, but the command failed: {LastError.Message}");

        var emitted = LastEvents.Select(e => e.Data).Where(d => d.GetType() == @event).ToList();
        if (emitted.Count == 0)
            throw new SpecAssertionException(
                $"Expected a {@event.Name} event, but the emitted events were: {describe(LastEvents.Select(e => e.Data))}");

        if (fields == null || fields.Rows.Count == 0) return;

        // Each expected row must equal one of the emitted events of this type.
        foreach (var row in fields.AsDictionaries())
        {
            var expected = RecordBuilding.Build(@event, row);
            if (!emitted.Any(e => Equals(e, expected)))
                throw new SpecAssertionException(
                    $"No emitted {@event.Name} equals the expected row.\n  expected: {expected}\n  emitted:  {describe(emitted)}");
        }
    }

    [Then("no events are emitted")]
    public void ThenNoEventsAreEmitted() => ThenNoEvents();

    [Then("validation fails with {string}")]
    public void ThenValidationFailsWith(string message) => ThenValidationFails(message);

    [Then("the command is refused")]
    public void ThenTheCommandIsRefused() => ThenCommandRefused();

    [Then("the {readmodel} read model contains")]
    public async Task ThenReadModelContains(Type readmodel, StepTable expected)
    {
        await Ctx.WaitForNonStaleProjectionsAsync(ProjectionTimeout, HostResource, StoreName);

        // Load through the concrete read-model type so LoadAsync<T> targets the right document table.
        var document = await loadReadModel(readmodel);

        if (document == null)
            throw new SpecAssertionException(
                $"Expected a {readmodel.Name} read model with id '{streamIdentity ?? StreamId}', but none exists.");

        Ctx.RecordTouchedType(readmodel);

        // One expected row of column = value; compare against the document's properties.
        var row = expected.AsDictionaries().FirstOrDefault()
                  ?? throw new SpecCriticalException($"'Then the {readmodel.Name} read model contains' needs at least one table row.");

        var failures = new List<string>();
        foreach (var (column, value) in row)
        {
            var property = readmodel.GetProperty(column);
            if (property == null)
            {
                failures.Add($"{column}: no such property on {readmodel.Name}");
                continue;
            }

            var actual = property.GetValue(document);
            var expectedValue = GherkinValue.Convert(value, property.PropertyType);
            if (!Equals(actual, expectedValue))
                failures.Add($"{column}: expected {value}, was {actual}");
        }

        if (failures.Count > 0)
            throw new SpecAssertionException($"{readmodel.Name} read model did not match: {string.Join("; ", failures)}");
    }

    [Then("{message} is sent")]
    public void ThenMessageIsSent(Type message)
    {
        if (LastSession == null)
            throw new SpecAssertionException(
                $"Expected a {message.Name} message to be sent, but no command has run (or it failed).");

        var sent = LastSession.Sent.AllMessages().Any(m => m.GetType() == message);
        if (!sent)
            throw new SpecAssertionException(
                $"Expected a {message.Name} message to be sent. Sent: {describe(LastSession.Sent.AllMessages())}");
    }

    // ---- plumbing -----------------------------------------------------------------------------

    private async Task executeCommandCore(object command)
    {
        var before = await fetchCurrentStreamAsync();

        // The command was dispatched either way — a validation rejection still received it,
        // and "this spec touched that command" is exactly what a sad-path scenario proves.
        Ctx.RecordTouchedType(command.GetType());

        try
        {
            var session = await Ctx.InvokeMessageAndWaitAsync(command, HostResource);
            var after = await fetchCurrentStreamAsync();
            LastEvents = after.Skip(before.Count).ToList();
            LastSession = session;
            LastError = null;

            // Observed run evidence (issue #107): the events the command actually appended and
            // the messages the tracked session actually sent — never what a Then merely names.
            recordTouched(LastEvents.Select(e => e.Data));
            recordTouched(session.Sent.AllMessages());
        }
        catch (Exception e)
        {
            LastError = e;
            LastEvents = [];
            LastSession = null;
        }
    }

    private void recordTouched(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            if (item != null) Ctx.RecordTouchedType(item.GetType());
        }
    }

    /// <summary>The current stream's events, by whichever identity kind the Given established.</summary>
    private Task<IReadOnlyList<IEvent>> fetchCurrentStreamAsync()
        => StreamKey is { } key
            ? Ctx.FetchEventStreamAsync(key, HostResource, StoreName)
            : Ctx.FetchEventStreamAsync(StreamId, HostResource, StoreName);

    private Task<object?> loadReadModel(Type readmodel)
    {
        // EventStoreAuthoring.LoadDocumentAsync is generic; close it over the read-model type so
        // LoadAsync<T> targets the correct document table.
        var method = typeof(EventStoreAuthoring).GetMethod(nameof(EventStoreAuthoring.LoadDocumentAsync))!
            .MakeGenericMethod(readmodel);
        var task = (Task)method.Invoke(null, [Ctx.EventStore(HostResource, StoreName), streamIdentity ?? StreamId, Ctx.Cancellation])!;
        return awaitAsObject(task);
    }

    private static async Task<object?> awaitAsObject(Task task)
    {
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private IReadOnlyList<object> buildEvents(Type aggregate, StepTable table)
    {
        var typeColumn = table.Headers.FirstOrDefault(h =>
            string.Equals(h, "Event", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h, "Type", StringComparison.OrdinalIgnoreCase))
            ?? throw new SpecCriticalException(
                "'Given events for …' needs an 'Event' column naming each row's event type; the other columns are its fields.");

        var events = new List<object>();
        foreach (var row in table.AsDictionaries())
        {
            var eventType = EventTypeResolver.Resolve(row[typeColumn], aggregate.Assembly);
            var fields = row.Where(kv => !string.Equals(kv.Key, typeColumn, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            events.Add(RecordBuilding.Build(eventType, fields));
        }

        return events;
    }

    private static IEnumerable<string> flatten(Exception ex)
    {
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.SelectMany(flatten);

        var messages = new List<string> { ex.Message };
        if (ex.InnerException != null) messages.AddRange(flatten(ex.InnerException));
        return messages;
    }

    private static string describe(IEnumerable<object> items)
        => "[" + string.Join(", ", items.Select(i => i?.ToString() ?? "null")) + "]";
}
