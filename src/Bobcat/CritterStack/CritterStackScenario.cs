using System.Globalization;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bobcat.CritterStack;

/// <summary>
/// The Given/When/Then vocabulary for an event-sourced application on Marten, Polecat or Fisher, over one
/// host and with no message bus (bobcat#471): arrange events and read models, act through the application's
/// own services, and assert what the act appended, what the aggregates and read models became, and how the
/// act was refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-only on purpose.</b> Plenty of applications use Marten without Wolverine, so nothing here needs
/// one. Every step reaches the store through the JasperFx.Events abstractions — the same scenario runs
/// against Marten, Polecat, Fisher or the in-memory store. WolverineFx.Bobcat's <c>WolverineScenario</c>
/// derives from this class and adds the acts that go through Wolverine's tracked session and the message
/// assertions; everything a store can answer lives here.
/// </para>
/// <para>
/// <b>Every step renders.</b> Each one records itself through <see cref="ScenarioRecorder" /> — the model a
/// projected Bobcat spec renders from — and reports its verdict as cells. Inside a recording scenario a wrong
/// is gathered on its step and the test carries on; outside one, the wrong is thrown
/// (<see cref="failureOf" />), so a plain test cannot pass by accident.
/// </para>
/// <para>
/// <b>Run evidence.</b> The types a scenario observably touched — the aggregates and events it arranged, the
/// events its acts appended, the read models it loaded — are recorded through
/// <see cref="ScenarioRecorder.RecordTouchedType" /> and travel on <c>scenario_finished.TouchedTypes</c>
/// (bobcat#465). Observed, never asserted: an event a Then step <i>expects</i> is not recorded.
/// </para>
/// </remarks>
public class CritterStackScenario
{
    /// <summary>
    /// How a refusal's reason is compared with the text the act raised. One place, so the case rule is a
    /// one-line change (bobcat#471 left exact-vs-ignore-case open).
    /// </summary>
    private const StringComparison ReasonComparison = StringComparison.Ordinal;

    private readonly string? _storeName;
    private readonly IHost? _host;
    private IEventStore? _store;
    private bool? _hasStore;
    private object? _stream;
    private CritterStackActOutcome _lastAct = CritterStackActOutcome.None;
    private bool _acted;

    /// <param name="host">The application under test.</param>
    /// <param name="storeName">Which event store to use, when the application has several. Null takes the only one.</param>
    public CritterStackScenario(IHost host, string? storeName = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Services = host.Services;
        _storeName = storeName;
    }

    /// <summary>
    /// A scenario over an application's services alone, for a test that has a container but no
    /// <see cref="IHost" /> — a hand-built <c>ServiceProvider</c>, or a host's <c>Services</c> handed on.
    /// <see cref="Host" /> throws on a scenario built this way.
    /// </summary>
    /// <param name="services">The application's services. Its event store is resolved from them.</param>
    /// <param name="storeName">Which event store to use, when the application has several. Null takes the only one.</param>
    public CritterStackScenario(IServiceProvider services, string? storeName = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _storeName = storeName;
    }

    /// <summary>
    /// The application under test. Throws when the scenario was built from an <see cref="IServiceProvider" />
    /// rather than a host; every step reads <see cref="Services" /> instead, so only a derived scenario that
    /// needs the host itself reaches for this.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scenario was built from an <see cref="IServiceProvider" />.</exception>
    public IHost Host => _host ?? throw new InvalidOperationException(
        "This scenario was built from an IServiceProvider, so it has no IHost. Read Services instead, or build the scenario from the host.");

    /// <summary>The application's services: the host's, or the provider the scenario was built from.</summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// How long a step that reads a projection waits for the async daemons to catch up. Twenty seconds by
    /// default — what a cold daemon on a loaded CI runner needs, and what the Gherkin fixture always waited.
    /// </summary>
    public TimeSpan ProjectionTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// What fills the members a partial object (<c>Specify&lt;T&gt;()</c> or a table row) leaves
    /// unspecified when the scenario builds it for a Given or a When. Null takes Bobcat's default
    /// (bobcat#421).
    /// </summary>
    public IUnspecifiedValues? UnspecifiedValues { get; set; }

    /// <summary>What the last act did.</summary>
    public CritterStackActOutcome LastAct
    {
        get => _lastAct;
        protected set
        {
            _lastAct = value ?? CritterStackActOutcome.None;
            _acted = !ReferenceEquals(_lastAct, CritterStackActOutcome.None);
        }
    }

    /// <summary>The application's event store. Throws, saying so, when it has none.</summary>
    public IEventStore Store => _store ??= Services.EventStore(_storeName);

    /// <summary>Whether the application has an event store at all. An application with none can still act.</summary>
    protected bool hasStore()
    {
        if (_hasStore is { } known) return known;

        try
        {
            _store ??= Services.EventStore(_storeName);
            _hasStore = true;
        }
        catch (Exception)
        {
            _hasStore = false;
        }

        return _hasStore.Value;
    }

    /// <summary>Clear the event store and forget the last act, for a suite that shares one host across scenarios.</summary>
    public virtual async Task ResetAsync(CancellationToken token = default)
    {
        if (Services.EventStores().Count > 0) await Services.ResetEventStoresAsync(token);
        _stream = null;
        LastAct = CritterStackActOutcome.None;
    }

    /// <summary>
    /// The exception a wrong is thrown as when no scenario is recording to gather it. A
    /// <see cref="SpecAssertionException" /> here; a derived scenario names its own.
    /// </summary>
    protected virtual Exception failureOf(string message) => new SpecAssertionException(message);

    // ---- arrange ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="GivenEvents{TAggregate}(Guid, object[])" /> for a stream identified by a strong-typed id around a
    /// Guid or a string (wolverine#4865), which reads by its own name.
    /// </summary>
    public Task GivenEvents<TAggregate>(object id, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), id, events);

    /// <summary>The stream the scenario acts against already holds exactly these events.</summary>
    public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    public Task GivenEvents<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEvents(typeof(TAggregate), key, events);

    /// <summary>
    /// The stream the scenario acts against already holds exactly these events, and has no aggregate
    /// type. Marten, Polecat and Fisher all allow a stream without one; this is the arrange for an event
    /// model that names none (GH-4892). Starts the stream when it does not exist, appends when it does.
    /// </summary>
    public Task GivenEvents(Guid id, params object[] events) => GivenEvents((Type?)null, id, events);

    /// <inheritdoc cref="GivenEvents(Guid, object[])" />
    public Task GivenEvents(string key, params object[] events) => GivenEvents((Type?)null, key, events);

    /// <inheritdoc cref="GivenEvents{TAggregate}(Guid, object[])" />
    /// <remarks>A null <paramref name="aggregate" /> arranges a stream with no aggregate type (GH-4892).</remarks>
    public async Task GivenEvents(Type? aggregate, object id, params object[] events)
    {
        var stream = streamName(aggregate, id);
        events = flatten(events);
        var (described, inline) = describe(events);
        var text = events.Length == 0 ? $"{stream} has no events yet" : $"{stream} has already recorded {described}";
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            // wolverine#4865: a strong-typed id is the Guid or string it wraps, to the store
            _stream = StrongTypedIds.StreamIdentity(id);
            if (aggregate is not null) recordTouched(aggregate);
            recordTouchedExpectations(events);
            if (!inline) recordValues("event", events);
            // Every store's StartStream(Type, ...) takes a null aggregate type as an untyped stream
            if (events.Length > 0) await EventStoreAuthoring.AppendAsync(Store, aggregate!, _stream, build(events, text));
        });
    }

    /// <summary>
    /// The stream the scenario acts against has no events yet. Appends nothing — it says so in the
    /// specification, and names the stream the act's events are read from.
    /// </summary>
    public Task GivenNoEventsFor<TAggregate>(Guid id) where TAggregate : class => GivenEvents(typeof(TAggregate), id);

    /// <inheritdoc cref="GivenNoEventsFor{TAggregate}(Guid)" />
    public Task GivenNoEventsFor<TAggregate>(object id) where TAggregate : class => GivenEvents(typeof(TAggregate), id);

    /// <inheritdoc cref="GivenNoEventsFor{TAggregate}(Guid)" />
    public Task GivenNoEventsFor<TAggregate>(string key) where TAggregate : class => GivenEvents(typeof(TAggregate), key);

    /// <summary>
    /// Arrange events on a <em>different</em> stream from the one the act runs against — a second
    /// aggregate for a rule that spans two, or history a read model fans in from.
    /// </summary>
    public Task GivenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    public Task GivenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), id, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    public Task GivenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => GivenEventsOn(typeof(TAggregate), key, events);

    /// <inheritdoc cref="GivenEventsOn{TAggregate}(object, object[])" />
    public async Task GivenEventsOn(Type aggregate, object id, params object[] events)
    {
        events = flatten(events);
        var (described, inline) = describe(events);
        var text = $"{streamName(aggregate, id)} has already recorded {described}";
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            recordTouched(aggregate);
            recordTouchedExpectations(events);
            if (!inline) recordValues("event", events);
            if (events.Length > 0)
                await EventStoreAuthoring.AppendAsync(Store, aggregate, StrongTypedIds.StreamIdentity(id), build(events, text));
        });
    }

    /// <summary>
    /// A read model already exists: <paramref name="document" /> is stored directly, as when an event
    /// model arranges a view rather than the events that build it.
    /// </summary>
    /// <remarks>
    /// <paramref name="document" /> may be a partial object, built with its unspecified members
    /// filled by <see cref="UnspecifiedValues" />. Storing it bypasses the projection, so a spec that
    /// is about how the view is built should arrange the events instead.
    /// </remarks>
    public async Task GivenReadModel<T>(object document) where T : class
    {
        var text = $"the {typeof(T).Name} read model is {describe([document]).Text}";
        await inStep(ScenarioRecorder.Step("Given", text), async () =>
        {
            var built = Build(document, text);
            if (built is not T)
                throw new SpecCriticalException(
                    $"'{text}': a {PartialMatching.ExpectedType(document).Name} cannot be stored as the {typeof(T).Name} read model.");

            recordTouched(typeof(T));
            await DocumentStores.StoreAllAsync(Store, typeof(T), [built]);
        });
    }

    /// <summary>
    /// How a stream reads in a step: its id is named after the aggregate (GH-4835), so a stream
    /// arranged once reads "the Order stream" — and a second one of the same type "Order Order2".
    /// </summary>
    private static string streamName(Type? aggregate, object id)
    {
        // A stream with no aggregate type (GH-4892) reads as "the stream", or "stream 2" for a second
        var label = aggregate?.Name ?? "stream";
        if (id is string key) return $"{(aggregate is null ? "stream" : aggregate.Name)} \"{key}\"";

        ScenarioValues.Learn(id, label);
        var name = ScenarioValues.Format(id);
        return name == label ? $"the {label}{(aggregate is null ? "" : " stream")}" : $"{label} {name}";
    }

    /// <summary>
    /// Values as a step's text shows them: each one in full — <c>Type(Property: value, …)</c> — when
    /// they fit on the line, otherwise only their type names, and the caller shows the values as a
    /// table under the step instead.
    /// </summary>
    protected static (string Text, bool Inline) describe(IReadOnlyList<object> values)
    {
        // A partial object reads as only the members it specifies: ShipmentConfirmed(TrackingNumber: 1Z999)
        var full = values.Any(x => x is IPartialObject or TaggedValue)
            ? string.Join(", ", values.Select(describeOne))
            : ScenarioValues.DescribeAll(values);
        return full.Length <= ScenarioValues.InlineLimit ? (full, true) : (names(values), false);
    }

    // One value as a step reads it; an event with tags says them after it (wolverine#4865)
    private static string describeOne(object value)
    {
        var described = value switch
        {
            IPartialObject specified => PartialObjects.Describe(specified),
            TaggedValue tagged => ScenarioValues.Describe(tagged.Value),
            _ => ScenarioValues.Describe(value)
        };

        var tags = Tagging.ExpectedTagsOf(value);
        return tags.Count == 0 ? described : $"{described} tagged {Tagging.Describe(tags)}";
    }

    /// <summary>
    /// The values a step was given, with each <em>list</em> of partial objects — the rows of a table
    /// through <c>PartialObjects.FromTable</c> — spread into its items, so a table and single values
    /// mix in one call.
    /// </summary>
    protected static object[] flatten(object[] values)
        => values.Any(x => x is IEnumerable<IPartialObject>)
            ? values.SelectMany(x => x is IEnumerable<IPartialObject> rows ? rows.Cast<object>() : [x]).ToArray()
            : values;

    /// <summary>A partial object built, its unspecified members filled by <see cref="UnspecifiedValues" />; anything else as it is.</summary>
    protected internal object Build(object value, string? step = null) => value switch
    {
        // wolverine#4865: an arranged event keeps the tags it names, so it is appended tagged
        IPartialObject specified when specified is ITaggedExpectation { ExpectedTags.Count: > 0 } tagged
            => Specifications.Tagged(PartialObjects.Build(specified, UnspecifiedValues, step), tagged.ExpectedTags.ToArray()),
        IPartialObject specified => PartialObjects.Build(specified, UnspecifiedValues, step),
        _ => value
    };

    private object[] build(object[] values, string step) => values.Select(x => Build(x, step)).ToArray();

    /// <summary>A type alone as an expectation: any one of them, whatever its values.</summary>
    protected static object anyOf(Type type) => new TablePartialObject(type, []);

    /// <summary>Values too long for the step's text, as a table under it: one row each, unjudged.</summary>
    protected static void recordValues(string noun, IReadOnlyList<object> values)
    {
        if (!recording || values.Count == 0) return;

        var run = new TableRun([noun, ObjectSetVerification.ValuesColumn]);
        for (var i = 0; i < values.Count; i++)
        {
            run.Cells.Add(new CellResult(noun, ResultStatus.ok, PartialMatching.ExpectedType(values[i]).Name) { RowIndex = i });
            run.Cells.Add(new CellResult(ObjectSetVerification.ValuesColumn, ResultStatus.ok,
                PartialMatching.DescribeExpected(values[i])) { RowIndex = i });
        }

        run.Report(null);
    }

    // ---- run evidence (bobcat#465) ---------------------------------------------------------

    /// <summary>Record that the scenario observably touched <paramref name="type" />. No-op outside a recording.</summary>
    protected static void recordTouched(Type type) => ScenarioRecorder.RecordTouchedType(type);

    /// <summary>Record the runtime type of each of <paramref name="values" /> as touched.</summary>
    protected static void recordTouched(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            if (value is not null) recordTouched(value.GetType());
        }
    }

    // An arranged value may be a partial object or a tagged one: the type it stands for is what was touched
    private static void recordTouchedExpectations(IEnumerable<object> values)
    {
        foreach (var value in values) recordTouched(PartialMatching.ExpectedType(value));
    }

    // ---- act -------------------------------------------------------------------------------

    /// <summary>
    /// Any act an application performs — a domain service, an endpoint called in-process, a background job
    /// run by hand — handed a fresh service scope from the host. What it appended is read from the store
    /// afterwards; an exception it throws is captured, not rethrown, so a refusal can be asserted with
    /// <see cref="ThenValidationFails" /> or <see cref="ThenRefusedWith(string)" />.
    /// </summary>
    /// <param name="description">How the step reads: <c>"the nightly sweep runs"</c>.</param>
    /// <param name="act">The act, given the scope's services.</param>
    public async Task WhenActing(string description, Func<IServiceProvider, Task> act)
    {
        ArgumentNullException.ThrowIfNull(act);
        // The act's own exception is captured, never thrown; the store reads around it can still throw
        await inStep(ScenarioRecorder.Step("When", description), () => recordActAsync(act));
    }

    /// <summary>
    /// Run <paramref name="act" /> and capture what it did. The seam every store-only act goes through:
    /// this one takes the store's high-water mark, runs the act in a new service scope, and reads back every
    /// event appended since — so <see cref="CritterStackActOutcome.NewEvents" /> and
    /// <see cref="CritterStackActOutcome.AllNewEvents" /> are the same list and nothing is told apart as a
    /// cascade. A derived scenario that can see more of the act, such as one that runs it inside a message
    /// bus's tracked session, overrides this. Never throws what the act threw.
    /// </summary>
    /// <remarks>
    /// Whole-store and by sequence: anything concurrent work appends while the act runs is counted as the
    /// act's, and an event appended to another tenant's database is not seen. No store reads an event's tags
    /// back, so the outcome says <see cref="CritterStackActOutcome.EventTagsKnown" /> is false, and a step
    /// that judges tags refuses with that reason rather than reporting every tagged event missing.
    /// </remarks>
    protected virtual async Task<CritterStackActOutcome> runActAsync(Func<IServiceProvider, Task> act)
    {
        var store = hasStore() ? Store : null;
        var floor = store is null ? 0 : await EventStores.HighWaterSequenceAsync(store);

        Exception? error = null;
        try
        {
            await using var scope = Services.CreateAsyncScope();
            await act(scope.ServiceProvider);
        }
        catch (Exception e)
        {
            error = e;
        }

        // Read even after a failure: an act can commit and then throw, and what it committed is a fact
        var appended = store is null
            ? (IReadOnlyList<IEvent>)Array.Empty<IEvent>()
            : (await EventStores.QueryEventsSinceAsync(store, floor + 1)).OrderBy(x => x.Sequence).ToList();

        return new CritterStackActOutcome(appended, error)
        {
            AllNewEvents = appended,
            StartedStreams = store is null ? Array.Empty<StreamAction>() : await startedStreamsOf(store, appended),
            // Read back from the store, which returns no tags
            EventTagsKnown = false
        };
    }

    // A stream whose first event is among those appended was started by the act. Its aggregate type comes from
    // the store's stream state: an event read back carries none (IEvent.AggregateTypeName is only filled inside
    // the async daemon), and without it every started stream would answer to every aggregate type
    private static async Task<IReadOnlyList<StreamAction>> startedStreamsOf(IEventStore store, IReadOnlyList<IEvent> appended)
    {
        var started = new List<StreamAction>();
        foreach (var e in appended.Where(x => x.Version == 1))
        {
            var action = e.StreamKey is { } key
                ? new StreamAction(key, StreamActionType.Start)
                : new StreamAction(e.StreamId, StreamActionType.Start);
            action.AggregateType = await aggregateTypeOf(store, e);
            started.Add(action);
        }

        return started;
    }

    // The aggregate type the store recorded for an event's stream, or null for a stream started without one or
    // an alias the store cannot map back to a type (Marten throws for one it has never seen)
    private static async Task<Type?> aggregateTypeOf(IEventStore store, IEvent @event)
    {
        try
        {
            var state = @event.StreamKey is { } key
                ? await EventStores.FetchStreamStateAsync(store, key)
                : await EventStores.FetchStreamStateAsync(store, @event.StreamId);
            return state?.AggregateType;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Run the act through the seam and remember it
    private async Task<CritterStackActOutcome> recordActAsync(Func<IServiceProvider, Task> act)
    {
        var outcome = await runActAsync(act);
        LastAct = outcome;
        recordTouched(outcome.NewEvents.Select(x => x.Data));
        return outcome;
    }

    // ---- a named stream, from a named version (wolverine#4938, bobcat#461) ---------------------
    // For an application that does not use Wolverine, or any act whose events nothing else can attribute:
    // the spec says WHICH stream and FROM WHICH version, and only what the stream gained past that version
    // counts. Store work only.

    /// <summary>
    /// The version the stream stands at now: its last event's version, or 0 when it has no events.
    /// Capture it before an act to name it in <see cref="ThenEventsOn{T}(Guid, long, object[])" />.
    /// Not a step: it reads, and the assertion that follows is the step.
    /// </summary>
    public Task<long> TheStreamVersion(Guid id) => streamVersion(id);

    /// <inheritdoc cref="TheStreamVersion(Guid)" />
    public Task<long> TheStreamVersion(string key) => streamVersion(key);

    /// <summary>
    /// The <typeparamref name="T" /> stream with this identity gained exactly these events, in this order,
    /// past <paramref name="afterVersion" /> — and nothing else. What it held at or before that version
    /// never counts, whoever appended it. Each expected event may be a whole object, an expected value or a
    /// partial object (<c>Specify&lt;T&gt;().With(...)</c>), judged on only the members it names.
    /// </summary>
    /// <remarks>
    /// Needs no act of this scenario's: the version is the spec's to name, or to capture with
    /// <see cref="TheStreamVersion(Guid)" /> before whatever acts. When the scenario's last act failed, the
    /// failure is reported instead of a missing event. A version past the stream's end is a
    /// <see cref="SpecCriticalException" />, never a vacuous pass.
    /// </remarks>
    public Task ThenEventsOn<T>(Guid id, long afterVersion, params object[] events) where T : class
        => thenEventsOn(typeof(T), id, afterVersion, flatten(events));

    /// <inheritdoc cref="ThenEventsOn{T}(Guid, long, object[])" />
    public Task ThenEventsOn<T>(string key, long afterVersion, params object[] events) where T : class
        => thenEventsOn(typeof(T), key, afterVersion, flatten(events));

    /// <summary>The <typeparamref name="T" /> stream with this identity gained nothing past <paramref name="afterVersion" />.</summary>
    public Task ThenNoEventsOn<T>(Guid id, long afterVersion) where T : class
        => thenEventsOn(typeof(T), id, afterVersion, []);

    /// <inheritdoc cref="ThenNoEventsOn{T}(Guid, long)" />
    public Task ThenNoEventsOn<T>(string key, long afterVersion) where T : class
        => thenEventsOn(typeof(T), key, afterVersion, []);

    /// <summary>
    /// One act and its verification, as one paragraph: capture the <typeparamref name="T" /> stream's
    /// version, run <paramref name="act" />, and check the stream gained exactly <paramref name="events" />
    /// past that version — <see cref="ThenEventsOn{T}(Guid, long, object[])" />'s check, with the version
    /// captured for you. No events means the act must append nothing to the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three sentences, each with its own verdict</b>, through <see cref="Paragraph" />: "Given the
    /// Wallet stream … is at version 2", "When …", "Then WalletCredited is emitted on it". The When reads
    /// <paramref name="act" />'s description from the overload that takes one.
    /// </para>
    /// <para>
    /// The act runs through the same seam as <see cref="WhenActing" />, so it is captured into
    /// <see cref="LastAct" /> — a failing act is reported by the verification, and a later
    /// <see cref="ThenValidationFails" /> can assert on it.
    /// </para>
    /// </remarks>
    public Task WhenActingOn<T>(Guid id, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => actingOn(typeof(T), id, null, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, Func{IServiceProvider, Task}, object[])" />
    public Task WhenActingOn<T>(string key, Func<IServiceProvider, Task> act, params object[] events) where T : class
        => actingOn(typeof(T), key, null, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, Func{IServiceProvider, Task}, object[])" />
    /// <param name="description">The act, as the When sentence says it: "the wallet is credited by hand".</param>
    public Task WhenActingOn<T>(Guid id, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => actingOn(typeof(T), id, description, act, flatten(events));

    /// <inheritdoc cref="WhenActingOn{T}(Guid, string, Func{IServiceProvider, Task}, object[])" />
    public Task WhenActingOn<T>(string key, string description, Func<IServiceProvider, Task> act, params object[] events)
        where T : class
        => actingOn(typeof(T), key, description, act, flatten(events));

    private Task thenEventsOn(Type aggregate, object id, long afterVersion, object[] expected)
    {
        var text = $"{describeEmitted(expected)} on the {aggregate.Name} stream {ScenarioValues.Format(id)} after version {afterVersion}";
        return inStep(ScenarioRecorder.Step("Then", text),
            () => verifyGained(aggregate, id, afterVersion, expected));
    }

    private async Task actingOn(Type aggregate, object id, string? description, Func<IServiceProvider, Task> act,
        object[] expected)
    {
        ArgumentNullException.ThrowIfNull(act);
        recordTouched(aggregate);
        ScenarioValues.Learn(id, aggregate.Name);

        var paragraph = new Paragraph("Given");
        var version = await streamVersion(id);
        using (paragraph.Sentence($"the {aggregate.Name} stream {ScenarioValues.Format(id)} is at version {version}"))
        {
        }

        await inStep(
            paragraph.Sentence("When", description ?? $"the {aggregate.Name} stream {ScenarioValues.Format(id)} is acted on"),
            () => recordActAsync(act));

        await inStep(paragraph.Sentence("Then", $"{describeEmitted(expected)} on it"),
            () => verifyGained(aggregate, id, version, expected));
    }

    private async Task verifyGained(Type aggregate, object id, long afterVersion, object[] expected)
    {
        var where = $"the {aggregate.Name} stream {ScenarioValues.Format(id)} after version {afterVersion}";
        if (afterVersion < 0)
            throw new SpecCriticalException($"A stream version is never negative, so nothing can be checked on {where}.");

        if (LastAct.Error is { } error)
        {
            fail($"Expected {(expected.Length == 0 ? "no events" : describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray()))} on {where}, "
                 + $"but the act failed: {error.GetType().Name}: {error.Message}");
            return;
        }

        var stream = await fetchAsync(Store, id);
        var current = stream.Count == 0 ? 0 : stream.Max(e => e.Version);

        // A version past the end makes every check vacuous: nothing can follow it yet
        if (afterVersion > current)
            throw new SpecCriticalException(
                $"The {aggregate.Name} stream {ScenarioValues.Format(id)} is at version {current}, so nothing can follow "
                + $"version {afterVersion} — the version the spec names is ahead of the stream.");

        recordTouched(aggregate);
        var gained = stream.Where(e => e.Version > afterVersion).ToList();
        var actual = gained.Select(e => e.Data).ToList();

        if (expected.Length == 0)
        {
            fact(actual.Count == 0, $"Expected no events on {where}, but it gained {ScenarioValues.DescribeAll(actual)}");
            return;
        }

        var run = ObjectSetVerification.Verify(actual, expected, "event", SetMode.Ordered, tagsOf(gained));
        if (recording) run.Report(null);

        fact(run.Succeeded,
            (actual.Count == 0
                ? $"Expected events on {where}, but it gained none (it is still at version {current}):"
                : $"The events on {where} were not exactly these, in this order:")
            + Environment.NewLine
            + string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")));
    }

    private async Task<long> streamVersion(object id)
    {
        var stream = await fetchAsync(Store, id);
        return stream.Count == 0 ? 0 : stream.Max(e => e.Version);
    }

    // A step (or a paragraph's sentence) that throws says so on its own line, then the failure carries on to
    // the runner. Every step whose body can throw while it is open goes through one of these: a bare
    // `using var step` ends as a pass whatever escapes it
    private static async Task inStep(IDisposable step, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    private static async Task<T> inStep<T>(IDisposable step, Func<Task<T>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    private static void inStep(IDisposable step, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            (step as IStepHandle)?.Fail(e);
            throw;
        }
        finally
        {
            step.Dispose();
        }
    }

    private static string describeEmitted(object[] expected)
        => expected.Length == 0
            ? "no events are emitted"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted";

    private static Task<IReadOnlyList<IEvent>> fetchAsync(IEventStore store, object id) => id switch
    {
        Guid guid => EventStores.FetchStreamAsync(store, guid),
        string key => EventStores.FetchStreamAsync(store, key),
        _ => throw new ArgumentOutOfRangeException(nameof(id), $"A stream is identified by a Guid or a string, not a {id.GetType().Name}")
    };

    // ---- assert: events ----------------------------------------------------------------------

    /// <summary>
    /// The tags each event was appended with, by the event's data — strong-typed ids, as the store tagged them.
    /// </summary>
    protected static Func<object, IReadOnlyList<object>?> tagsOf(IReadOnlyList<IEvent> events)
    {
        var tags = new Dictionary<object, IReadOnlyList<object>>(ReferenceEqualityComparer.Instance);
        foreach (var e in events)
        {
            tags[e.Data] = e.Tags?.Select(x => x.Value).ToArray() ?? Array.Empty<object>();
        }

        return data => tags.GetValueOrDefault(data);
    }

    /// <summary>
    /// Whether a step about tags can be judged: false, with the reason recorded, when the act's events do not
    /// carry their tags (<see cref="CritterStackActOutcome.EventTagsKnown" />) and the step names some.
    /// </summary>
    protected bool tagsCanBeJudged(IEnumerable<object> expected, bool queryByTags = false)
    {
        if (LastAct.EventTagsKnown) return true;
        if (!queryByTags && !expected.Any(x => Tagging.ExpectedTagsOf(x).Count > 0)) return true;

        return fail("The act's events were read back from the event store, which does not return the tags they were " +
                    "appended with, so a step about tags cannot judge them. Name the stream instead — " +
                    "ThenEventsOn<TAggregate>(id, ...) — or read the boundary's model with TheDcbModel<T>(query).");
    }

    /// <summary>The act appended exactly these event types, in this order.</summary>
    public void ThenEvents(params Type[] events)
    {
        using var step = ScenarioRecorder.Step("Then", $"{names(events)} {(events.Length == 1 ? "is" : "are")} emitted");
        if (!actSucceeded()) return;

        var actual = LastAct.NewEvents.Select(x => x.Data.GetType()).ToArray();
        for (var i = 0; i < Math.Max(actual.Length, events.Length); i++)
        {
            check("event", i < actual.Length ? actual[i].Name : null, i < events.Length ? events[i].Name : null, i);
        }

        fact(actual.SequenceEqual(events),
            $"Expected {describeTypes(events)} but the act appended {describeTypes(actual)}");
    }

    /// <summary>
    /// The act appended exactly these events, in order, compared <b>structurally</b> — property by
    /// property, nested properties and collections included, never through <c>Equals</c> (GH-4835).
    /// Wrap one in <see cref="Expect.Value{T}" /> to show but not judge a member nobody controls.
    /// </summary>
    /// <remarks>
    /// An event may also be a partial object — <c>Specify&lt;T&gt;().With(x =&gt; x.Prop, value)</c>, or the
    /// rows of <c>PartialObjects.FromTable</c> — judged and shown on only the members it names (wolverine#4870).
    /// Prefer a deterministic clock to <c>Ignoring(...)</c> for a timestamp.
    /// </remarks>
    public void ThenEvents(params object[] events)
        => verifyEvents(flatten(events), SetMode.Ordered, "{0} {1} emitted");

    /// <summary>
    /// The act appended exactly these events, in <b>any</b> order — whole objects, <see cref="Expect.Value{T}" />s
    /// or partial objects, as for <see cref="ThenEvents(object[])" />.
    /// </summary>
    public void ThenEventsInAnyOrder(params object[] events)
        => verifyEvents(flatten(events), SetMode.AnyOrder, "{0} {1} emitted in any order");

    /// <summary>The act appended exactly these event types, in any order.</summary>
    public void ThenEventsInAnyOrder(params Type[] events)
        => verifyEvents(events.Select(anyOf).ToArray(), SetMode.AnyOrder, "{0} {1} emitted in any order");

    /// <summary>
    /// The act appended these events among whatever else it appended — whole objects, <see cref="Expect.Value{T}" />s
    /// or partial objects. Nothing else is judged, or shown.
    /// </summary>
    public void ThenEmitted(params object[] events)
        => verifyEvents(flatten(events), SetMode.Contains, "{0} {1} emitted");

    /// <summary>The act appended a <typeparamref name="T" />, whatever its values, among whatever else it appended.</summary>
    public void ThenEmitted<T>() => verifyEvents([anyOf(typeof(T))], SetMode.Contains, "{0} {1} emitted");

    /// <summary>The act appended no <typeparamref name="T" /> at all.</summary>
    public void ThenNotEmitted<T>() => verifyAbsent([typeof(T)]);

    /// <summary>
    /// The act appended nothing matching any of these — a partial object forbids only the events that
    /// agree on the members it names, and a whole object only an equal one.
    /// </summary>
    public void ThenNotEmitted(params object[] events) => verifyAbsent(flatten(events));

    private void verifyEvents(object[] expected, SetMode mode, string format)
    {
        var text = string.Format(CultureInfo.InvariantCulture, format,
            describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray()), expected.Length == 1 ? "is" : "are");
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected)) return;

        verifySet("event", LastAct.NewEvents.Select(x => x.Data).ToArray(), expected, mode, tagsOf(LastAct.NewEvents));
    }

    private void verifyAbsent(object[] forbidden)
    {
        using var step = ScenarioRecorder.Step("Then",
            $"{describeTypes(forbidden.Select(PartialMatching.ExpectedType).ToArray())} {(forbidden.Length == 1 ? "is" : "are")} not emitted");
        if (!actSucceeded() || !tagsCanBeJudged(forbidden)) return;

        var run = ObjectSetVerification.Absent(LastAct.NewEvents.Select(x => x.Data).ToArray(), forbidden, "event",
            tagsOf(LastAct.NewEvents));
        if (recording) run.Report(null);

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, "event")));
    }

    /// <summary>
    /// GH-4920. The act appended exactly these events, in order, to <typeparamref name="TAggregate" />'s stream
    /// with this identity — for a command that decides against several streams, where <c>ThenEvents</c> can
    /// only speak for the one the act addresses. Only what the act added counts, never what was arranged.
    /// </summary>
    public void ThenEventsOn<TAggregate>(object id, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), id, flatten(events));

    /// <inheritdoc cref="ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(object id) where TAggregate : class => verifyEventsOn(typeof(TAggregate), id, []);

    /// <inheritdoc cref="ThenEventsOn{TAggregate}(object, object[])" />
    public void ThenEventsOn<TAggregate>(Guid id, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), id, flatten(events));

    /// <inheritdoc cref="ThenEventsOn{TAggregate}(object, object[])" />
    public void ThenEventsOn<TAggregate>(string key, params object[] events) where TAggregate : class
        => verifyEventsOn(typeof(TAggregate), key, flatten(events));

    /// <summary>GH-4920. The act appended nothing to <typeparamref name="TAggregate" />'s stream with this identity.</summary>
    public void ThenNoEventsOn<TAggregate>(Guid id) where TAggregate : class => verifyEventsOn(typeof(TAggregate), id, []);

    /// <inheritdoc cref="ThenNoEventsOn{TAggregate}(Guid)" />
    public void ThenNoEventsOn<TAggregate>(string key) where TAggregate : class => verifyEventsOn(typeof(TAggregate), key, []);

    private void verifyEventsOn(Type aggregate, object id, object[] expected)
    {
        var text = expected.Length == 0
            ? $"no events are emitted on the {aggregate.Name} stream {ScenarioValues.Format(id)}"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on the {aggregate.Name} stream {ScenarioValues.Format(id)}";
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected)) return;

        var stream = StrongTypedIds.StreamIdentity(id);
        var onStream = LastAct.AllNewEvents.Where(x => isOn(x, stream)).Select(x => x.Data).ToArray();
        if (expected.Length == 0)
        {
            fact(onStream.Length == 0,
                $"Expected no events on the {aggregate.Name} stream {id}, but the act appended {ScenarioValues.DescribeAll(onStream)}");
            return;
        }

        if (onStream.Length == 0)
        {
            // Say where the act's events DID go, which is usually the whole answer
            var elsewhere = LastAct.AllNewEvents
                .GroupBy(x => (object?)x.StreamKey ?? x.StreamId)
                .Select(x => $"{x.Key}: {ScenarioValues.DescribeAll(x.Select(e => e.Data))}")
                .ToList();
            fact(false, elsewhere.Count == 0
                ? $"Expected events on the {aggregate.Name} stream {id}, but the act appended no events anywhere"
                : $"Expected events on the {aggregate.Name} stream {id}, but the act appended none there. It appended to: {string.Join("; ", elsewhere)}");
            return;
        }

        verifySet("event", onStream, expected, SetMode.Ordered, tagsOf(LastAct.AllNewEvents));
    }

    /// <summary>
    /// wolverine#4865. The act appended exactly these events, in order, among those <paramref name="query" />
    /// selects by their tags — the DCB counterpart to <see cref="ThenEventsOn{TAggregate}(Guid, object[])" />,
    /// for a decision whose consistency boundary is a tag query rather than one stream. Only what the act
    /// added counts, its cascades' included.
    /// </summary>
    public void ThenEventsOn(EventTagQuery query, params object[] events)
    {
        var expected = flatten(events);
        var scope = EventStores.Describe(query);
        var text = expected.Length == 0
            ? $"no events are emitted on {scope}"
            : $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on {scope}";
        using var step = ScenarioRecorder.Step("Then", text);
        if (!actSucceeded() || !tagsCanBeJudged(expected, queryByTags: true)) return;

        var selected = LastAct.AllNewEvents.Where(x => selects(query, x)).ToList();
        if (expected.Length == 0)
        {
            fact(selected.Count == 0,
                $"Expected no events on {scope}, but the act appended {ScenarioValues.DescribeAll(selected.Select(x => x.Data))}");
            return;
        }

        verifySet("event", selected.Select(x => x.Data).ToArray(), expected, SetMode.Ordered, tagsOf(selected));
    }

    /// <summary>wolverine#4865. The act appended nothing that <paramref name="query" /> selects.</summary>
    public void ThenNoEventsOn(EventTagQuery query) => ThenEventsOn(query);

    // Whether a tag query selects an event: one of its conditions names a tag the event carries, and
    // either no event type or the event's own
    private static bool selects(EventTagQuery query, IEvent @event)
        => @event.Tags is { Count: > 0 } tags && query.Conditions.Any(condition =>
            (condition.EventType is null || condition.EventType == @event.Data.GetType())
            && tags.Any(tag => tag.TagType == condition.TagType && Equals(tag.Value, condition.TagValue)));

    /// <summary>Whether <paramref name="event" /> is on the stream <paramref name="id" /> — a Guid or a string key.</summary>
    protected static bool isOn(IEvent @event, object? id) => id switch
    {
        Guid guid => @event.StreamId == guid,
        string key => @event.StreamKey == key,
        _ => false
    };

    /// <summary>The act appended nothing — the refusal half of a guard.</summary>
    public void ThenNoEvents()
    {
        using var step = ScenarioRecorder.Step("Then", "no events are emitted");

        // Not actSucceeded(): a refused or throwing act that appended nothing is exactly what this step passes
        if (!_acted)
        {
            fail("Expected the act to append no events, but no act has run.");
            return;
        }

        fact(LastAct.NewEvents.Count == 0,
            $"Expected no events but the act appended {ScenarioValues.DescribeAll(LastAct.NewEvents.Select(x => x.Data))}");
    }

    /// <summary>
    /// The one event of type <typeparamref name="T" /> the act appended, to assert on further. Not a
    /// step: it reads the capture, and the assertion that follows is the step. Throws unless exactly
    /// one was appended.
    /// </summary>
    public T TheEvent<T>()
    {
        var matching = LastAct.NewEvents.Select(x => x.Data).OfType<T>().ToArray();
        if (matching.Length == 1) return matching[0];

        throw failureOf($"Expected exactly one {typeof(T).Name} but the act appended {matching.Length}{failureSuffix()}");
    }

    /// <summary>Every event the act appended, for a test that wants to look for itself.</summary>
    public IReadOnlyList<object> TheEvents => LastAct.NewEvents.Select(x => x.Data).ToList();

    /// <summary>
    /// The act started a <typeparamref name="TAggregate" /> stream with this identity. The identity is
    /// usually the decision — a redelivered trigger that reuses an id collides instead of starting a
    /// second stream — and <c>ThenEvents</c> cannot say where its events went.
    /// </summary>
    public Task ThenStreamIsStarted<TAggregate>(object id) where TAggregate : class => ThenStreamIsStarted(typeof(TAggregate), id);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    public Task ThenStreamIsStarted<TAggregate>(Guid id) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), id);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    public Task ThenStreamIsStarted<TAggregate>(string key) where TAggregate : class
        => ThenStreamIsStarted(typeof(TAggregate), key);

    /// <inheritdoc cref="ThenStreamIsStarted{TAggregate}(object)" />
    public async Task ThenStreamIsStarted(Type aggregate, object id)
    {
        await inStep(ScenarioRecorder.Step("Then", $"a {aggregate.Name} stream is started with id \"{id}\""), async () =>
        {
            if (!actSucceeded()) return;

            var events = await fetchAsync(Store, StrongTypedIds.StreamIdentity(id));
            fact(events.Count > 0,
                $"Expected a {aggregate.Name} stream with id {id}, but no stream exists there. The act appended its events somewhere else, or started no stream at all.");
        });
    }

    /// <summary>
    /// wolverine#4865. One paragraph, two sentences: "a <typeparamref name="TAggregate" /> stream is started" and
    /// "these events are emitted on it", for a command whose handler starts the stream with an id it assigns —
    /// read back from the act (<see cref="TheStartedStream{TAggregate}" />). Each sentence has its own verdict.
    /// </summary>
    public Task ThenStreamIsStartedWithEvents<TAggregate>(params object[] events) where TAggregate : class
        => streamIsStartedWithEvents(typeof(TAggregate), flatten(events));

    // A spec that knows the id already says ThenStreamIsStarted<T>(id) and ThenEventsOn<T>(id, ...); an id
    // overload here would read a lone event as the id

    private async Task streamIsStartedWithEvents(Type aggregate, object[] expected)
    {
        var paragraph = new Paragraph("Then");
        var stream = await inStep<object?>(paragraph.Sentence($"a {aggregate.Name} stream is started"), async () =>
        {
            if (!actSucceeded()) return null;

            var started = startedStreamOrNull(aggregate);
            if (started is null) return null;

            ScenarioValues.Learn(started, aggregate.Name);
            var events = await fetchAsync(Store, started);
            fact(events.Count > 0,
                $"Expected a {aggregate.Name} stream with id {ScenarioValues.Format(started)}, but no stream exists there.");
            return started;
        });
        if (stream is null) return;

        var text = $"{describeTypes(expected.Select(PartialMatching.ExpectedType).ToArray())} {(expected.Length == 1 ? "is" : "are")} emitted on it";
        using (paragraph.Sentence(text))
        {
            var onStream = LastAct.AllNewEvents.Where(x => isOn(x, stream)).ToList();
            verifySet("event", onStream.Select(x => x.Data).ToArray(), expected, SetMode.Ordered, tagsOf(onStream));
        }
    }

    // The one stream of this type the act started, or null with the reason recorded as the step's failure
    private object? startedStreamOrNull(Type aggregate)
    {
        var started = LastAct.StartedStreams.Where(x => x.AggregateType is null || x.AggregateType == aggregate).ToList();
        if (started.Count == 1) return started[0].Key ?? (object)started[0].Id;

        fail(started.Count == 0
            ? $"The act started no {aggregate.Name} stream{(LastAct.StartedStreams.Count == 0 ? "; it started no stream at all" : "")}."
            : $"The act started {started.Count} {aggregate.Name} streams; name the one you mean with ThenStreamIsStartedWithEvents<{aggregate.Name}>(id, ...).");
        return null;
    }

    /// <summary>The write model, folded from its stream.</summary>
    public async Task<T?> TheAggregate<T>(Guid id) where T : class
    {
        recordTouched(typeof(T));
        return await EventStores.AggregateStreamAsync<T>(Store, id);
    }

    /// <inheritdoc cref="TheAggregate{T}(Guid)" />
    public async Task<T?> TheAggregate<T>(string key) where T : class
    {
        recordTouched(typeof(T));
        return await EventStores.AggregateStreamAsync<T>(Store, key);
    }

    /// <inheritdoc cref="TheAggregate{T}(Guid)" />
    public Task<T?> TheAggregate<T>(object id) where T : class => StrongTypedIds.StreamIdentity(id) switch
    {
        Guid guid => TheAggregate<T>(guid),
        var key => TheAggregate<T>((string)key)
    };

    /// <summary>
    /// GH-4931. The id the last act assigned to the <typeparamref name="TAggregate" /> stream it started — for
    /// a command that starts a stream, whose id a spec cannot know before the act.
    /// </summary>
    /// <remarks>
    /// A started stream's aggregate type is read back from the store's stream state. A stream started with no
    /// aggregate type, or one whose type the store cannot map back to a .NET type, answers to any
    /// <typeparamref name="TAggregate" />.
    /// </remarks>
    /// <exception cref="SpecCriticalException">The act started no such stream, or more than one.</exception>
    public Guid TheStartedStream<TAggregate>() where TAggregate : class => startedStream(typeof(TAggregate)).Id;

    /// <inheritdoc cref="TheStartedStream{TAggregate}()" />
    public string TheStartedStreamKey<TAggregate>() where TAggregate : class
        => startedStream(typeof(TAggregate)).Key
           ?? throw new SpecCriticalException($"The {typeof(TAggregate).Name} stream the act started is identified by a Guid, not a string key. Use TheStartedStream<{typeof(TAggregate).Name}>().");

    private StreamAction startedStream(Type aggregate)
    {
        // A stream started with no aggregate type (Storage.StartStream with events only), or one whose type the
        // store could not map back, can still be the one
        var started = LastAct.StartedStreams.Where(x => x.AggregateType is null || x.AggregateType == aggregate).ToList();
        return started.Count switch
        {
            1 => started[0],
            0 => throw new SpecCriticalException(LastAct.StartedStreams.Count == 0
                ? $"The act started no {aggregate.Name} stream; it started no stream at all."
                : $"The act started no {aggregate.Name} stream. It started: {string.Join(", ", LastAct.StartedStreams.Select(x => x.AggregateType?.Name ?? "(no aggregate type)"))}."),
            _ => throw new SpecCriticalException($"The act started {started.Count} {aggregate.Name} streams, so there is no one id to name.")
        };
    }

    // ---- assert: refusals --------------------------------------------------------------------

    /// <summary>
    /// The act was refused, and the refusal says <paramref name="reason" />: an HTTP endpoint's
    /// ProblemDetails, or the exception the act threw — searched with its inner exceptions, and every one
    /// of an <see cref="AggregateException" />'s, so a refusal wrapped by the act's plumbing is still found.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is empty or whitespace, which every refusal contains.</exception>
    public void ThenRefusedWith(string reason) => assertRefusal($"refused with \"{reason}\"", reason);

    /// <summary>
    /// The act was refused with <paramref name="reason" />, and the refusal names each of
    /// <paramref name="mentioning" />: the values an event model gives a refusal, as in
    /// <c>EmailAlreadyInUse { email: joe@example.com }</c>.
    /// </summary>
    /// <remarks>
    /// Checked against the refusal's text, an HTTP endpoint's ProblemDetails or the exception the act
    /// threw, so it holds for both and asks nothing of how the refusal is built.
    /// </remarks>
    public void ThenRefusedWith(string reason, params object[] mentioning)
    {
        requireReason(reason);
        if (mentioning.Length == 0)
        {
            ThenRefusedWith(reason);
            return;
        }

        var named = string.Join(", ", mentioning.Select(ScenarioValues.Format));
        using var step = ScenarioRecorder.Step("Then", $"refused with \"{reason}\", naming {named}");

        var refusal = refusalText(reason);
        if (refusal is null)
        {
            fail($"Expected the act to be refused with \"{reason}\", but it succeeded.");
            return;
        }

        var hasReason = reasonMatches(refusal, reason);
        cell("reason", hasReason, reason, refusal);
        var missing = new List<string>();
        foreach (var value in mentioning)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var isNamed = refusal.Contains(text, StringComparison.Ordinal);
            cell(ScenarioValues.Format(value), isNamed, "named", isNamed ? "named" : "not named");
            if (!isNamed) missing.Add(text);
        }

        fact(hasReason && missing.Count == 0,
            $"Expected a refusal containing \"{reason}\" and naming {string.Join(", ", mentioning)}, but it was: {refusal}");
    }

    /// <summary>
    /// The act was refused by throwing a <typeparamref name="TException" />, found anywhere in the
    /// exception the act raised (an inner exception, or one of an <see cref="AggregateException" />'s),
    /// and it matches <paramref name="expected" /> when one is given: a partial object naming the
    /// members a spec is about, as in <c>Specify&lt;EmailAlreadyInUse&gt;().With(x =&gt; x.Email, …)</c>.
    /// </summary>
    public TException? ThenRefusedWith<TException>(object? expected = null) where TException : Exception
    {
        if (expected is not null and not IPartialObject)
            throw new ArgumentException(
                $"Describe the expected {typeof(TException).Name} as a partial object, Specify<{typeof(TException).Name}>().With(...): an exception is matched on the members a spec names, never built whole.",
                nameof(expected));

        var partial = expected as IPartialObject;
        var text = partial is null || partial.Values.Count == 0
            ? $"refused with {typeof(TException).Name}"
            : $"refused with {PartialObjects.Describe(partial)}";
        using var step = ScenarioRecorder.Step("Then", text);

        if (LastAct.Error is null)
        {
            fail(LastAct.Refusal is { } refusal
                ? $"Expected a {typeof(TException).Name} to be thrown, but the act was refused with a response: {refusal}"
                : $"Expected a {typeof(TException).Name} to be thrown, but the act succeeded.");
            return null;
        }

        var thrown = exceptionsIn(LastAct.Error).OfType<TException>().FirstOrDefault();
        // Matched on the search, not the name: an ArgumentNullException is the ArgumentException a spec asked for
        cell("exception", thrown is not null, typeof(TException).Name, thrown?.GetType().Name ?? LastAct.Error.GetType().Name);
        if (thrown is null)
        {
            fact(false,
                $"Expected a {typeof(TException).Name}, but the act threw {LastAct.Error.GetType().Name}: {LastAct.Error.Message}");
            return null;
        }

        if (partial is not null)
        {
            var run = PropertyCells.Verify(thrown, partial);
            if (!run.Succeeded && !recording)
                throw failureOf($"{typeof(TException).Name} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }

        return thrown;
    }

    /// <summary>The exception and every exception inside it: inner exceptions, and each of an <see cref="AggregateException" />'s.</summary>
    protected static IEnumerable<Exception> exceptionsIn(Exception exception)
    {
        yield return exception;

        IEnumerable<Exception> inner = exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } one } => [one],
            _ => []
        };
        foreach (var child in inner)
        foreach (var nested in exceptionsIn(child))
            yield return nested;
    }

    /// <summary>
    /// The act failed validation with <paramref name="reason" />. The same check as
    /// <see cref="ThenRefusedWith(string)" />, worded for a command.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reason" /> is empty or whitespace, which every refusal contains.</exception>
    public void ThenValidationFails(string reason) => assertRefusal($"validation fails with \"{reason}\"", reason);

    /// <summary>
    /// The act was cleanly refused (issue #168): it ran, nothing threw, and it appended nothing — the sad path
    /// of a handler that refuses without throwing, such as a messaging railway's <c>Stop</c>. A refusal that
    /// answers, such as an HTTP endpoint's 4xx, is refused too.
    /// </summary>
    /// <remarks>
    /// Deliberately reason-less: a clean stop carries no reason anywhere an act surfaces, so a reason clause
    /// would assert on nothing. A throwing refusal is <see cref="ThenValidationFails" /> or
    /// <see cref="ThenRefusedWith(string)" />.
    /// </remarks>
    public void ThenCommandRefused()
    {
        using var step = ScenarioRecorder.Step("Then", "the command is refused");

        if (!_acted)
        {
            fail("Expected the command to be refused, but no act has run.");
            return;
        }

        if (LastAct.Error is { } error)
        {
            fail($"Expected the command to be cleanly refused, but it threw: {error.Message}. " +
                 "A throwing refusal is what ThenValidationFails or ThenRefusedWith describes.");
            return;
        }

        if (LastAct.Refusal is not null) return;

        fact(LastAct.NewEvents.Count == 0,
            $"Expected the command to be refused, but it emitted {LastAct.NewEvents.Count} event(s): " +
            ScenarioValues.DescribeAll(LastAct.NewEvents.Select(e => e.Data)));
    }

    private void assertRefusal(string text, string reason)
    {
        requireReason(reason);
        using var step = ScenarioRecorder.Step("Then", text);

        var refusal = refusalText(reason);
        if (refusal is null)
        {
            fail($"Expected the act to be refused with \"{reason}\", but it succeeded.");
            return;
        }

        var matched = reasonMatches(refusal, reason);
        cell("reason", matched, reason, refusal);
        fact(matched, $"Expected a refusal containing \"{reason}\" but it was: {refusal}");
    }

    /// <summary>
    /// The text a refusal is judged on: an answer's body, else the first message in the act's exception — or
    /// any of its inner exceptions — that names <paramref name="reason" />, else all of them. Null when the act
    /// was not refused.
    /// </summary>
    private string? refusalText(string reason)
    {
        if (LastAct.Refusal is { } answer) return answer;
        if (LastAct.Error is not { } error) return null;

        var messages = exceptionsIn(error).Select(x => x.Message).Where(x => x.Length > 0).Distinct().ToList();
        return messages.FirstOrDefault(x => reasonMatches(x, reason))
               ?? (messages.Count == 0 ? error.Message : string.Join(" | ", messages));
    }

    private static bool reasonMatches(string refusal, string reason) => refusal.Contains(reason, ReasonComparison);

    // Every refusal's text contains the empty string, so a blank reason would pass on any refusal at all
    private static void requireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A refusal's reason must say something: an empty or whitespace reason matches every refusal.",
                nameof(reason));
    }

    // ---- assert: messages --------------------------------------------------------------------

    /// <summary>
    /// The last act caused at least <paramref name="count" /> messages of type <typeparamref name="T" /> to be
    /// sent, read from <see cref="CritterStackActOutcome.MessagesSent" />. A store-only act tracks no
    /// messages, so this needs a scenario whose act does, such as WolverineFx.Bobcat's.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is less than 1, which any act satisfies.</exception>
    public void ThenMessagesSent<T>(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        using var step = ScenarioRecorder.Step("Then",
            count == 1 ? $"{typeof(T).Name} is sent" : $"at least {count} {typeof(T).Name} are sent");
        if (!_acted)
        {
            fail($"Expected {count} message(s) of type {typeof(T).Name} to be sent, but no act has run.");
            return;
        }

        if (!actSucceeded()) return;

        var sent = LastAct.MessagesSent.OfType<T>().Count();
        cell("sent", sent >= count, count == 1 ? "at least one" : $"at least {count}", sent);
        fact(sent >= count, $"Expected at least {count} message(s) of type {typeof(T).Name} to be sent, but {sent} were.");
    }

    // ---- assert: read models -----------------------------------------------------------------

    /// <summary>
    /// GH-4921. The aggregate of <typeparamref name="T" />'s stream with this identity, as the store serves it
    /// through <c>FetchLatest&lt;T&gt;</c>: a projected snapshot where there is one, else built from the stream,
    /// whatever the projection lifecycle. For state built from events. A plain or async-projected document
    /// is <see cref="ThenDocument{T}(object)" />. Throws when there is no such stream.
    /// </summary>
    public Task<T> ThenAggregate<T>(Guid id) where T : class => thenAggregate<T>(id, null);

    /// <inheritdoc cref="ThenAggregate{T}(Guid)" />
    public Task<T> ThenAggregate<T>(object id) where T : class => thenAggregate<T>(id, null);

    /// <inheritdoc cref="ThenAggregate{T}(Guid)" />
    public Task<T> ThenAggregate<T>(string key) where T : class => thenAggregate<T>(key, null);

    /// <summary>
    /// GH-4921. The aggregate, as <see cref="ThenAggregate{T}(Guid)" />, judged against
    /// <paramref name="expected" />: a partial object (<c>Specify&lt;T&gt;().With(...)</c>, a table, or
    /// assertions on its members), or a whole object or <see cref="Expect.Value{T}" /> structurally.
    /// </summary>
    public Task<T> ThenAggregate<T>(Guid id, object expected) where T : class => thenAggregate<T>(id, expected);

    /// <inheritdoc cref="ThenAggregate{T}(Guid, object)" />
    public Task<T> ThenAggregate<T>(object id, object expected) where T : class => thenAggregate<T>(id, expected);

    /// <inheritdoc cref="ThenAggregate{T}(Guid, object)" />
    public Task<T> ThenAggregate<T>(string key, object expected) where T : class => thenAggregate<T>(key, expected);

    private Task<T> thenAggregate<T>(object id, object? expected) where T : class
    {
        // The step opens before the load, so a missing aggregate fails a rendered step
        return inStep(ScenarioRecorder.Step("Then", expected is null ? existsText<T>(id) : matchesText(expected)), async () =>
        {
            var aggregate = StrongTypedIds.StreamIdentity(id) switch
            {
                Guid guid => await EventStores.FetchLatestAsync<T>(Store, guid),
                string key => await EventStores.FetchLatestAsync<T>(Store, key),
                _ => throw new ArgumentOutOfRangeException(nameof(id), "A stream is identified by a Guid or a string")
            } ?? throw failureOf(
                $"No {typeof(T).Name} with id {id}: FetchLatest found no stream there. The act appended to another stream, or none.");

            recordTouched(typeof(T));
            if (expected is not null) judge(aggregate, expected);
            return aggregate;
        });
    }

    private static string existsText<T>(object id) => $"there is a {typeof(T).Name} {ScenarioValues.Format(id)}";

    /// <summary>
    /// GH-4921. The document of type <typeparamref name="T" /> with this identity, loaded with
    /// <c>LoadAsync</c> once the projections have caught up: a plain document, or a projection stored as
    /// one. An aggregate built from events is <see cref="ThenAggregate{T}(Guid)" />. The same as
    /// <see cref="ThenReadModel{T}(object)" />.
    /// </summary>
    public Task<T> ThenDocument<T>(object id) where T : class => ThenReadModel<T>(id);

    /// <summary>GH-4921. The document, as <see cref="ThenDocument{T}(object)" />, judged against <paramref name="expected" />.</summary>
    public Task<T> ThenDocument<T>(object id, object expected) where T : class => ThenReadModel<T>(id, expected);

    /// <summary>
    /// The read model with this identity, once the projections have caught up — an async projection
    /// read straight after the act reads a stale document. Throws when there is no such document,
    /// because there is nothing to hand back. Loaded with <c>LoadAsync</c>, so it is the same as
    /// <see cref="ThenDocument{T}(object)" />; for an aggregate built from events, see
    /// <see cref="ThenAggregate{T}(Guid)" /> (GH-4921).
    /// </summary>
    public Task<T> ThenReadModel<T>(object id) where T : class => thenReadModel<T>(id, null);

    /// <summary>
    /// The read model with this identity, once the projections have caught up, judged against
    /// <paramref name="expected" />: a partial object (<c>Specify&lt;T&gt;()</c>) on only the members it
    /// names, or a whole object or <see cref="Expect.Value{T}" /> structurally. Hands the document back.
    /// </summary>
    public Task<T> ThenReadModel<T>(object id, object expected) where T : class => thenReadModel<T>(id, expected);

    private async Task<T> thenReadModel<T>(object id, object? expected) where T : class
    {
        await ThenProjectionsAreCaughtUp(typeof(T));

        // The step opens before the load, so a missing document fails a rendered step
        return await inStep(ScenarioRecorder.Step("Then", expected is null ? existsText<T>(id) : matchesText(expected)), async () =>
        {
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(Store, id)
                           ?? throw failureOf(
                               $"No {typeof(T).Name} document with id {id}. The projections caught up, so either nothing routed to this id or its identity rule differs.");
            recordTouched(typeof(T));
            if (expected is not null) judge(document, expected);
            return document;
        });
    }

    /// <summary>
    /// No read model with this identity exists once the projections have caught up — a view the
    /// events did not create, or one they deleted.
    /// </summary>
    public async Task ThenNoReadModel<T>(object id) where T : class
    {
        await ThenProjectionsAreCaughtUp(typeof(T));

        await inStep(ScenarioRecorder.Step("Then", $"there is no {typeof(T).Name} {ScenarioValues.Format(id)}"), async () =>
        {
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(Store, id);
            fact(document is null,
                $"Expected no {typeof(T).Name} document with id {id}, but there is one: {ScenarioValues.Describe(document)}");
        });
    }

    /// <summary>
    /// The <typeparamref name="T" /> of the stream this scenario is about — the one the act started, or else the
    /// one the givens arranged — as the store serves it through <c>FetchLatest&lt;T&gt;</c> once the async
    /// daemons have caught up, judged against <paramref name="expected" /> when one is given.
    /// </summary>
    /// <remarks>
    /// Only for a single-stream projection, registered or self-aggregating: a multi-stream projection has no
    /// one stream to be fetched by, so it is refused (wolverine#4865). For a document stored any other way,
    /// <see cref="ThenSingleDocument{T}" />.
    /// </remarks>
    public async Task<T> ThenSingleReadModel<T>(object? expected = null) where T : class
    {
        var kind = await EventStores.ProjectionKindAsync(Store, typeof(T));
        if (kind is not null && kind != SubscriptionType.SingleStreamProjection)
            throw new SpecCriticalException(
                $"ThenSingleReadModel<{typeof(T).Name}>() reads a single-stream projection, but {typeof(T).Name} is a {kind}. " +
                $"Name the document instead: ThenReadModel<{typeof(T).Name}>(id, ...).");

        await AfterAsyncDaemonsCatchUpAsync();

        return await inStep(ScenarioRecorder.Step("Then", expected is null ? singleText<T>() : matchesText(expected)), async () =>
        {
            var stream = scenarioStream(typeof(T), nameof(ThenSingleReadModel));
            var model = await theLatest<T>(stream)
                        ?? throw failureOf(
                            $"No {typeof(T).Name} for the stream {ScenarioValues.Format(stream)}: FetchLatest found nothing there.");

            recordTouched(typeof(T));
            if (expected is not null) judge(model, expected);
            return model;
        });
    }

    private static string singleText<T>() => $"there is a {typeof(T).Name} for the scenario's stream";

    /// <summary>
    /// The <typeparamref name="T" /> document stored under the id of the stream this scenario is about, loaded with
    /// <c>LoadAsync</c> once the async daemons have caught up — a projected document on Marten or Polecat — and
    /// judged against <paramref name="expected" /> when one is given.
    /// </summary>
    public async Task<T> ThenSingleDocument<T>(object? expected = null) where T : class
    {
        await AfterAsyncDaemonsCatchUpAsync();

        return await inStep(ScenarioRecorder.Step("Then", expected is null ? singleText<T>() : matchesText(expected)), async () =>
        {
            var stream = scenarioStream(typeof(T), nameof(ThenSingleDocument));
            var document = await EventStoreAuthoring.LoadDocumentAsync<T>(Store, stream)
                           ?? throw failureOf(
                               $"No {typeof(T).Name} document with id {ScenarioValues.Format(stream)}. The async daemons caught up, so nothing stored one there.");

            recordTouched(typeof(T));
            if (expected is not null) judge(document, expected);
            return document;
        });
    }

    private Task<T?> theLatest<T>(object stream) where T : class => stream switch
    {
        Guid guid => EventStores.FetchLatestAsync<T>(Store, guid),
        _ => EventStores.FetchLatestAsync<T>(Store, (string)stream)
    };

    // The stream a spec without an id means: the one stream the act started, else the one the givens arranged
    private object scenarioStream(Type model, string step)
    {
        if (LastAct.StartedStreams is [var started]) return started.Key ?? (object)started.Id;
        return _stream ?? throw new SpecCriticalException(
            $"{step}<{model.Name}>() reads the stream the scenario is about, but the act started none and the givens arranged none. " +
            $"Name it: ThenReadModel<{model.Name}>(id, ...).");
    }

    /// <summary>
    /// Wait until every asynchronous projection on every database has caught up, so a read model reflects the
    /// act — what the <c>WaitForNonStaleResults</c>-style helpers need first. Waits up to
    /// <see cref="ProjectionTimeout" />.
    /// </summary>
    public async Task AfterAsyncDaemonsCatchUpAsync()
    {
        await inStep(ScenarioRecorder.Step("Given", "the async daemons have caught up"),
            () => EventStores.WaitForNonStaleProjectionsAsync(Store, ProjectionTimeout));
    }

    /// <summary><see cref="AfterAsyncDaemonsCatchUpAsync()" /> for one tenant's database.</summary>
    public async Task AfterAsyncDaemonsCatchUpAsync(string tenantId)
    {
        await inStep(ScenarioRecorder.Step("Given", $"the async daemons have caught up for tenant \"{tenantId}\""),
            () => EventStores.WaitForNonStaleProjectionsAsync(Store, tenantId, ProjectionTimeout));
    }

    /// <summary>There is no <typeparamref name="T" /> at all once the projections have caught up.</summary>
    public async Task ThenNoReadModel<T>() where T : class
    {
        await ThenProjectionsAreCaughtUp(typeof(T));

        await inStep(ScenarioRecorder.Step("Then", $"there is no {typeof(T).Name}"), async () =>
        {
            var documents = await allDocuments<T>();
            fact(documents.Count == 0,
                $"Expected no {typeof(T).Name} document, but there {(documents.Count == 1 ? "is one" : $"are {documents.Count}")}: {ScenarioValues.DescribeAll(documents.Cast<object>().ToList())}");
        });
    }

    private async Task<IReadOnlyList<T>> allDocuments<T>() where T : class
    {
        var session = DocumentStores.SessionFactoryFor(Store).QuerySession();
        try
        {
            var queryable = ((JasperFx.Events.Documents.IDocumentReadOperations)session).Query<T>();
            return await JasperFx.Events.Documents.DocumentQueryableExtensions.ToListAsync(queryable);
        }
        finally
        {
            if (session is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (session is IDisposable disposable) disposable.Dispose();
        }
    }

    /// <summary>
    /// The projections that build <paramref name="readModel" /> have caught up: every async daemon is waited
    /// on, up to <see cref="ProjectionTimeout" />.
    /// </summary>
    public async Task ThenProjectionsAreCaughtUp(Type readModel)
    {
        await inStep(ScenarioRecorder.Step("Then", $"the {readModel.Name} read model has caught up"),
            () => EventStores.WaitForNonStaleProjectionsAsync(Store, ProjectionTimeout));
    }

    /// <summary>
    /// wolverine#4865. <typeparamref name="T" /> projected over the events <paramref name="query" /> selects by
    /// their tags — a Dynamic Consistency Boundary model, or a view built the same way. Null when nothing
    /// matches the query.
    /// </summary>
    public async Task<T?> TheDcbModel<T>(EventTagQuery query) where T : class
    {
        recordTouched(typeof(T));
        return await EventStores.AggregateByTagsAsync<T>(Store, query);
    }

    /// <summary>
    /// wolverine#4865. <typeparamref name="T" />, projected over the events <paramref name="query" /> selects by
    /// their tags, is <paramref name="expected" />: a whole object, an <see cref="Expect.Value{T}" /> or a partial
    /// one, judged as <see cref="ThenMatches" /> judges.
    /// </summary>
    public async Task ThenDcbModel<T>(EventTagQuery query, object expected) where T : class
    {
        var text = $"the {typeof(T).Name} over {EventStores.Describe(query)} matches";
        var model = await inStep(ScenarioRecorder.Step("Given", $"{typeof(T).Name} is projected over {EventStores.Describe(query)}"),
            () => TheDcbModel<T>(query));

        matches(text, model, expected);
    }

    // ---- assert: any object (GH-4836) --------------------------------------------------------

    /// <summary>
    /// Verify selected properties of <paramref name="subject" /> as a table — Bobcat's
    /// <c>PropertyCells</c>, one cell per column, nested paths (<c>Address.City</c>) included. A
    /// response, a read model, an aggregate, anything.
    /// </summary>
    /// <param name="subject">What is being verified.</param>
    /// <param name="expected">One header row naming the properties, one row of expected values: <c>"| Name | Address.City |\n| Ann | Austin |"</c>.</param>
    public void Verify(object subject, StepTable expected)
    {
        using var step = ScenarioRecorder.Step("Then", $"the {subject.GetType().Name} has");
        var run = PropertyCells.Verify(subject, expected);
        if (!run.Succeeded && !recording)
        {
            throw failureOf($"{subject.GetType().Name} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }
    }

    /// <summary>
    /// <paramref name="subject" /> is structurally equal to <paramref name="expected" /> — or to an
    /// <see cref="Expect.Value{T}" /> with members to ignore. The same rules as <see cref="ThenEvents(object[])" />.
    /// </summary>
    public void ThenMatches(object? subject, object expected)
        => matches(null, subject, expected);

    private void matches(string? text, object? subject, object expected)
    {
        using var step = ScenarioRecorder.Step("Then", text ?? matchesText(expected));
        judge(subject, expected);
    }

    // How a step that judges an object against expected reads
    private static string matchesText(object expected)
        => $"the {(expected is IPartialObject partial ? partial.Type : unwrap(expected).GetType()).Name} matches";

    // Judge subject against expected on the step in progress: a partial object on the members it names, else structurally
    private void judge(object? subject, object expected)
    {
        if (expected is IPartialObject partial)
        {
            if (subject is null)
            {
                fail($"Expected a {partial.Type.Name} but there was nothing to verify");
                return;
            }

            var run = PropertyCells.Verify(subject, partial);
            if (!run.Succeeded && !recording)
            {
                throw failureOf($"{partial.Type.Name} did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
            }

            return;
        }

        compareSequence(unwrap(expected).GetType().Name, subject is null ? Array.Empty<object>() : new[] { subject }, new[] { expected });
    }

    // ---- the clock (ClockGrammars, as recorded steps) ------------------------------------------
    // Each freezes or moves the ambient BobcatClock, which is what relative TODAY/NOW cells resolve against
    // and what an application reads when its TimeProvider is BobcatClock.Current. Synchronous on purpose: the
    // clock is AsyncLocal, and a value set inside an async method never flows back to its caller.

    /// <summary>The ambient clock a step's relative values resolve against: <see cref="BobcatClock.Current" />.</summary>
    public TimeProvider Clock => BobcatClock.Current;

    /// <summary>Freeze the clock at midnight UTC on <paramref name="date" />.</summary>
    public void GivenTheDateIs(DateOnly date)
    {
        using var step = ScenarioRecorder.Step("Given", $"the date is {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        BobcatClock.Controllable().SetUtcNow(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    /// <summary>Freeze the clock at <paramref name="now" />.</summary>
    public void GivenTheTimeIs(DateTimeOffset now)
    {
        using var step = ScenarioRecorder.Step("Given", $"the current time is {formatInstant(now)}");
        BobcatClock.Controllable().SetUtcNow(now.ToUniversalTime());
    }

    /// <summary>Move the clock forward by <paramref name="duration" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration" /> is negative: the clock only advances.</exception>
    public void WhenTheClockAdvancesBy(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        using var step = ScenarioRecorder.Step("When", $"the clock advances by {formatDuration(duration)}");
        BobcatClock.Controllable().Advance(duration);
    }

    /// <summary><paramref name="duration" /> passes: the clock moves forward by it.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration" /> is negative: time only passes forward.</exception>
    public void WhenTimePasses(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        using var step = ScenarioRecorder.Step("When", $"{formatDuration(duration)} passes");
        BobcatClock.Controllable().Advance(duration);
    }

    /// <summary>Move the clock to <paramref name="instant" />.</summary>
    public void WhenTheClockAdvancesTo(DateTimeOffset instant)
    {
        using var step = ScenarioRecorder.Step("When", $"the clock advances to {formatInstant(instant)}");
        BobcatClock.Controllable().SetUtcNow(instant.ToUniversalTime());
    }

    // "2026-01-02", "2026-01-02 09:15", "2026-01-02 09:15:30.25": UTC, to the tick, in the form ClockGrammars
    // reads back. No zone suffix, because its parser assumes UTC and refuses one it does not know
    private static string formatInstant(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var format = utc.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd"
            : utc.Ticks % TimeSpan.TicksPerMinute == 0 ? "yyyy-MM-dd HH:mm"
            : utc.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd HH:mm:ss"
            : "yyyy-MM-dd HH:mm:ss.FFFFFFF";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    // "3 days", "1 hour 30 minutes", "0.5 milliseconds": the words FriendlyTimeSpanParser reads back, to the
    // tick. Never negative; the clock steps refuse one before they get here
    private static string formatDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero) return "0 seconds";

        var parts = new List<string>();
        void add(decimal amount, string unit)
        {
            if (amount != 0)
                parts.Add($"{amount.ToString("0.####", CultureInfo.InvariantCulture)} {unit}{(amount == 1 ? "" : "s")}");
        }

        add(duration.Days, "day");
        add(duration.Hours, "hour");
        add(duration.Minutes, "minute");
        add(duration.Seconds, "second");
        // The milliseconds and whatever is under them: a tick is 0.0001 of a millisecond
        add((decimal)(duration.Ticks % TimeSpan.TicksPerSecond) / TimeSpan.TicksPerMillisecond, "millisecond");
        return string.Join(" ", parts);
    }

    // ---- shared ------------------------------------------------------------------------------

    private static bool recording => ScenarioRecorder.Current is not null;

    /// <summary>Report a wrong: gathered on the step in progress, or thrown when nothing records. Returns false.</summary>
    protected bool fail(string message)
    {
        if (!recording) throw failureOf(message);

        SpecAssert.Fail(message);
        return false;
    }

    /// <summary>Report <paramref name="because" /> unless <paramref name="condition" /> holds.</summary>
    protected bool fact(bool condition, string because) => condition || fail(because);

    // A judged cell: expected against actual, recorded on the step in progress
    private static bool check(string name, object? actual, object? expected, int rowIndex = -1)
    {
        var matched = ObjectComparison.LeafEquals(actual, expected);
        cell(name, matched, expected, actual, rowIndex);
        return matched;
    }

    private static void cell(string name, bool matched, object? expected, object? actual, int rowIndex = -1)
    {
        if (!recording) return;

        ScenarioRecorder.RecordCell(new CellResult(name, matched ? ResultStatus.success : ResultStatus.failed)
        {
            Expected = ObjectComparison.Format(expected),
            Actual = ObjectComparison.Format(actual),
            RowIndex = rowIndex
        });
    }

    private static void valueCell(string name, object? actual, int rowIndex = -1)
    {
        if (!recording) return;
        ScenarioRecorder.RecordCell(new CellResult(name, ResultStatus.ok, ObjectComparison.Format(actual))
        {
            RowIndex = rowIndex
        });
    }

    private static bool row(IReadOnlyList<ObjectComparison.Leaf> leaves, int rowIndex)
    {
        foreach (var leaf in leaves)
        {
            if (leaf.Ignored) valueCell(leaf.Path, leaf.Actual, rowIndex);
            else cell(leaf.Path, leaf.Matched, leaf.Expected, leaf.Actual, rowIndex);
        }

        return leaves.All(x => x.Matched);
    }

    private static string describeLeaves(IEnumerable<ObjectComparison.Leaf> leaves)
        => string.Join("; ", leaves.Where(x => !x.Matched)
            .Select(x => $"{x.Path}: expected {ObjectComparison.Format(x.Expected)}, was {ObjectComparison.Format(x.Actual)}"));

    private void compareSequence(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected)
    {
        var problems = new List<string>();

        for (var i = 0; i < Math.Max(actual.Count, expected.Count); i++)
        {
            var expectedValue = i < expected.Count ? unwrap(expected[i]) : null;
            var actualValue = i < actual.Count ? actual[i] : null;

            check(noun, actualValue?.GetType().Name, expectedValue?.GetType().Name, i);

            if (expectedValue is null)
            {
                problems.Add($"[{i}] an extra {actualValue!.GetType().Name}");
                continue;
            }

            if (actualValue is null)
            {
                problems.Add($"[{i}] {expectedValue.GetType().Name} is missing");
                continue;
            }

            if (actualValue.GetType() != expectedValue.GetType())
            {
                problems.Add($"[{i}] expected {expectedValue.GetType().Name} but was {actualValue.GetType().Name}");
                continue;
            }

            var leaves = ObjectComparison.Compare(actualValue, expectedValue, (expected[i] as IExpectedValue)?.IgnoredPaths);
            if (!row(leaves, i)) problems.Add($"[{i}] {expectedValue.GetType().Name}: {describeLeaves(leaves)}");
        }

        fact(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>An expected value's value; anything else as it is.</summary>
    protected static object unwrap(object value) => value is IExpectedValue expected ? expected.Value : value;

    /// <summary>
    /// <paramref name="actual" /> against <paramref name="expected" /> as a set verification (GH-4835):
    /// one grid with a row per item — OK, a FAIL naming the values that disagree, MISSING, EXTRA, or
    /// ORDER — rather than a positional comparison that reads one missing event as every later one wrong.
    /// </summary>
    protected void verifySet(string noun, IReadOnlyList<object> actual, IReadOnlyList<object> expected, SetMode mode,
        Func<object, IReadOnlyList<object>?>? tags = null)
    {
        var run = tags is null
            ? ObjectSetVerification.Verify(actual, expected, noun, mode)
            : ObjectSetVerification.Verify(actual, expected, noun, mode, tags);

        if (recording) run.Report(null);

        fact(run.Succeeded, string.Join(Environment.NewLine, ObjectSetVerification.Problems(run, noun)));
    }

    /// <summary>
    /// A failure the step did not ask about is reported as the act failing, rather than as "expected 1
    /// event, got 0" — which would send the reader to the handler's logic when the answer is in the
    /// exception the act captured. No act at all is a failure too, so a forgotten When cannot pass a step
    /// that looks for nothing.
    /// </summary>
    protected bool actSucceeded()
    {
        // Without an act every "what the act did" step would pass on the empty outcome
        if (!_acted)
        {
            return fail("Expected an act to judge, but no act has run. A step about what the act did needs a When first.");
        }

        if (LastAct.Error is { } error)
        {
            return fail($"The act failed: {error.GetType().Name}: {error.Message}");
        }

        if (LastAct.Refusal is { } refusal)
        {
            return fail($"The act was refused: {refusal}");
        }

        return true;
    }

    /// <summary>What the act's failure or refusal was, as a sentence's tail, or nothing when it succeeded.</summary>
    protected string failureSuffix()
        => LastAct.Error is { } e ? $". The act failed: {e.GetType().Name}: {e.Message}"
            : LastAct.Refusal is { } r ? $". The act was refused: {r}" : "";

    private static string names(IReadOnlyList<object> values) => describeTypes(values.Select(PartialMatching.ExpectedType).ToArray());

    private static string names(Type[] types) => describeTypes(types);

    /// <summary>Type names as a step reads them: <c>A, B</c>, or <c>nothing</c>.</summary>
    protected static string describeTypes(IReadOnlyList<Type> types)
        => types.Count == 0 ? "nothing" : string.Join(", ", types.Select(x => x.Name));
}
