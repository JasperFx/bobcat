using System.Text.RegularExpressions;

namespace Bobcat.EventModel.Emlang;

/// <summary>
/// One <c>--aggregate Slice=Type</c> override: the aggregate a slice decides against, said outright.
/// An override always wins over what <see cref="AggregateInference"/> would infer (bobcat#444).
/// </summary>
/// <param name="Slice">The slice's name, as the import names it (<c>ConfirmAppointment</c>).</param>
/// <param name="Aggregate">The aggregate type's name. Repeat the override for a slice that draws on several.</param>
public sealed record AggregateOverride(string Slice, string Aggregate)
{
    /// <summary>
    /// bobcat#454: the slice <b>starts</b> this aggregate's stream, said outright with
    /// <c>--starts-stream Slice=Type</c>. The slice's <c>--aggregate</c> overrides are then the
    /// aggregates it decides against besides.
    /// </summary>
    public bool StartsStream { get; init; }

    /// <summary>Read <c>Slice=Type</c>; throws <see cref="FormatException"/> on anything else.</summary>
    public static AggregateOverride Parse(string text)
    {
        var parts = (text ?? "").Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw new FormatException($"'{text}' is not an aggregate override. Write it as Slice=Type, e.g. ConfirmAppointment=Appointment.");
        }

        return new AggregateOverride(parts[0], parts[1]);
    }

    /// <summary>Read a <c>--starts-stream Slice=Type</c> override; throws <see cref="FormatException"/> on anything else.</summary>
    public static AggregateOverride ParseStartsStream(string text) => Parse(text) with { StartsStream = true };
}

/// <summary>
/// Gives every command an aggregate (bobcat#444). A board rarely says which stream an event is
/// stored on, and an emlang swimlane is an actor, not a stream (#439) — but a command that does not
/// only start a stream needs either a DCB decider or one or more single-stream aggregates, so an
/// aggregate-less handler is no default at all. This infers one from the examples and says so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Streams come from lineage.</b> An example that gives an event and expects another is a claim
/// that the second is appended where the first was: the two are joined into one stream. Only when
/// their subjects share a word, though — an example arranging two streams for one decision
/// ("an approved volunteer accepts a home check assignment") must not fold the volunteer's
/// application and the home check into a single aggregate. The events one slice emits are one
/// stream too.
/// </para>
/// <para>
/// <b>A stream is named for the subject most of its events share</b>: the longest run of words that
/// a majority of its events open their name with (<c>Appointment</c> from <c>AppointmentConfirmed</c>,
/// <c>AppointmentCancelled</c>, <c>HomeCheckAppointmentProposed</c> …). A stream no example links to
/// any other joins one whose name its subject ends with — <c>FosterHandoverAppointment</c> is an
/// <c>Appointment</c> — and that join is reported as made by name alone.
/// </para>
/// <para>
/// <b>A declaration always wins</b>: an eventmodelers.ai element's <c>aggregate</c>, or an
/// <see cref="AggregateOverride"/>. Neither is joined to a stream with a different declared name.
/// </para>
/// <para>
/// <b>Every guess is called out</b>, in the report and as a <c>// ⚠ inferred</c> comment on the
/// slice in the generated definition, so a wrong one is a one-line edit there. A command left with
/// no aggregate is reported as missing, never quietly made aggregate-less.
/// </para>
/// </remarks>
public static class AggregateInference
{
    private const string MultiStreamCallout =
        "⚠ draws on several streams ({0}): choose several [WriteModel] IEventStream<T> parameters or a DCB decider (bobcat#443).";

    /// <summary>
    /// Fill in <see cref="ImportedEventModel.EventStreams"/>, and each command or automation slice's
    /// <see cref="CuratedSlice.StartsStream"/>, <see cref="CuratedSlice.Aggregates"/>,
    /// <see cref="CuratedSlice.Inferred"/> and <see cref="CuratedSlice.Callouts"/>.
    /// </summary>
    /// <param name="declared">Event name → the stream the model declares for it.</param>
    /// <param name="overrides">The <c>--aggregate</c> overrides, which win over everything.</param>
    /// <param name="report">Where every inference and gap is said, one line each.</param>
    public static void Apply(ImportedEventModel model, IReadOnlyDictionary<string, string> declared,
        IReadOnlyList<AggregateOverride>? overrides, List<string> report)
        => Apply(model, declared, overrides, report, null);

    /// <inheritdoc cref="Apply(ImportedEventModel, IReadOnlyDictionary{string, string}, IReadOnlyList{AggregateOverride}?, List{string})"/>
    /// <param name="unimported">
    /// Event name → the chapter it appears in, for the board's chapters that were not imported
    /// (bobcat#454). Names the chapter in a <see cref="ImportCalloutCodes.StreamStartFromUnimportedChapter"/> callout.
    /// </param>
    /// <returns>Every callout, as <see cref="ImportCallout"/>s with a stable code (bobcat#454).</returns>
    public static IReadOnlyList<ImportCallout> Apply(ImportedEventModel model, IReadOnlyDictionary<string, string> declared,
        IReadOnlyList<AggregateOverride>? overrides, List<string> report, IReadOnlyDictionary<string, string>? unimported)
    {
        overrides ??= [];
        unimported ??= new Dictionary<string, string>();
        var callouts = new List<ImportCallout>();
        foreach (var unknown in overrides.Where(o => model.Slices.All(s => s.Name != o.Slice)))
        {
            report.Add($"⚠ --aggregate {unknown.Slice}={unknown.Aggregate}: no slice is named '{unknown.Slice}', so it changes nothing.");
        }

        var streams = new Streams();

        // Every event the model names is in some stream, in the order the model names it
        foreach (var slice in model.Slices)
        {
            foreach (var name in slice.Events.Concat(slice.ConsumedEvents)) streams.Add(Name(name));
            foreach (var scenario in slice.Specifications?.Scenarios ?? [])
            {
                foreach (var given in scenario.Given) streams.Add(Name(given.Event));
                foreach (var then in scenario.Then.Where(x => x.Event is not null)) streams.Add(Name(then.Event!));
            }
        }

        foreach (var (name, stream) in declared) streams.Pin(Name(name), Name(stream), declaredBy: "the model declares it");
        foreach (var slice in model.Slices)
        {
            if (overrides.Where(o => o.Slice == slice.Name).OrderByDescending(o => o.StartsStream).FirstOrDefault() is not { } first) continue;
            foreach (var name in slice.Events) streams.Pin(Name(name), Name(first.Aggregate), declaredBy: $"--aggregate {first.Slice}={first.Aggregate}");
        }

        // A slice's events are one stream, and an example's given and expected events are one
        // stream when their subjects share a word
        foreach (var slice in model.Slices.Where(isDecider))
        {
            var emitted = slice.Events.Select(Name).Where(x => x.Length > 0).ToList();
            for (var i = 1; i < emitted.Count; i++) streams.Join(emitted[0], emitted[i], lineage: false);

            // A slice said outright to start its stream (--starts-stream) has nothing earlier on it, so
            // what its examples give first is on some other stream, never joined to the one it starts
            if (overrides.Any(o => o.Slice == slice.Name && o.StartsStream)) continue;

            foreach (var scenario in actingScenarios(slice))
            {
                var expected = scenario.Then.Where(x => x.Event is not null).Select(x => Name(x.Event!)).ToList();
                var givens = scenario.Given.Select(x => Name(x.Event)).Where(x => x.Length > 0).Distinct().ToList();

                // One subject among the givens: the example is about one stream, and what it expects
                // is appended there whatever it is called (ItemAdded on the Order). Several: each
                // expected event joins only the givens it shares a subject with.
                var oneStream = givens.All(a => givens.All(b => a == b || SharesSubject(a, b)));
                foreach (var given in givens)
                {
                    foreach (var then in expected)
                    {
                        if (SharesSubject(given, then))
                        {
                            streams.Join(given, then, lineage: true);
                        }
                        else if (oneStream && streams.StreamOf(given) != streams.StreamOf(then))
                        {
                            streams.Join(given, then, lineage: true);
                            report.Add($"⚠ inferred: {then} is on the same stream as {given}, because an example of '{slice.Name}' gives one and expects the other, though their names share no subject.");
                        }
                    }
                }
            }
        }

        streams.NameAndMerge(report);

        model.EventStreams.Clear();
        foreach (var (name, stream) in streams.Assignments()) model.EventStreams[name] = stream;

        var emittedByAny = model.Slices.SelectMany(x => x.Events).Select(Name).ToHashSet(StringComparer.Ordinal);
        foreach (var slice in model.Slices.Where(isDecider))
        {
            var context = new Assignment(slice, streams, overrides.Where(o => o.Slice == slice.Name).ToList(), report, callouts, emittedByAny, unimported);
            assign(context);
        }

        return callouts;
    }

    private sealed record Assignment(CuratedSlice Slice, Streams Streams, List<AggregateOverride> Overrides, List<string> Report,
        List<ImportCallout> Callouts, HashSet<string> Emitted, IReadOnlyDictionary<string, string> Unimported)
    {
        /// <summary>Say a callout on the slice (the generated definition writes it as a comment) and in the callout list.</summary>
        public void Add(ImportCallout callout)
        {
            Slice.Callouts.Add(callout.Message);
            Callouts.Add(callout);
        }
    }

    private static bool isDecider(CuratedSlice slice)
        => slice.Pattern is "Command" or "Automation" || (slice.Pattern is null && slice.Command is not null);

    private static IEnumerable<CuratedScenario> actingScenarios(CuratedSlice slice)
        => (slice.Specifications?.Scenarios ?? []).Where(x => x.When is not null);

    private static void assign(Assignment context)
    {
        var (slice, streams, overrides, report, _, emitted, unimported) = context;
        slice.Aggregates.Clear();
        slice.StartsStream = null;
        slice.Inferred.Clear();
        slice.Callouts.Clear();

        var scenarios = actingScenarios(slice).ToList();
        var written = slice.Events.Select(Name).Select(streams.StreamOf).OfType<Stream>().Distinct().ToList();
        var read = scenarios.SelectMany(x => x.Given).Select(x => streams.StreamOf(Name(x.Event))).OfType<Stream>()
            .Distinct().Where(x => !written.Contains(x)).ToList();

        IReadOnlyList<string> eventsOf(Stream stream) => slice.Events.Select(Name).Where(x => streams.StreamOf(x) == stream).Distinct().ToList();
        IReadOnlyList<string> givenOn(Stream stream) => scenarios.SelectMany(x => x.Given).Select(x => Name(x.Event))
            .Where(x => streams.StreamOf(x) == stream).Distinct().ToList();
        IReadOnlyList<string> examplesGiving(Stream stream) => scenarios
            .Where(x => x.Given.Any(g => streams.StreamOf(Name(g.Event)) == stream)).Select(x => x.Name).ToList();

        // A stream the slice writes is one it starts when an example acts on it with nothing given there
        Stream? starts = null;
        foreach (var stream in written)
        {
            var givenThere = scenarios.Any(x => x.Given.Any(g => streams.StreamOf(Name(g.Event)) == stream));
            if (scenarios.Count > 0 && !givenThere && starts is null)
            {
                starts = stream;
                continue;
            }

            if (stream.Name is { } name) add(slice, name);
            if (scenarios.Count == 0 && stream.Name is { } assumed && !stream.IsDeclared)
            {
                context.Add(new ImportCallout(slice.Name, ImportCalloutCodes.StreamStartUnknown, [assumed],
                    $"⚠ no example says whether it starts the {assumed} stream, so it is assumed to append to one.",
                    $"--starts-stream {slice.Name}={assumed}") { Events = eventsOf(stream) });
            }
        }

        if (starts?.Name is { } started) slice.StartsStream = started;
        foreach (var stream in read.Where(x => x.Name is not null)) add(slice, stream.Name!);

        if (overrides.Count > 0)
        {
            // Said outright, so it wins. With --starts-stream, exactly that stream is started and the
            // --aggregate ones are decided against; with --aggregate alone, exactly those, the first
            // started when the examples say so
            var overridden = overrides.Where(x => !x.StartsStream).Select(x => Name(x.Aggregate)).ToList();
            var startsOverridden = overrides.FirstOrDefault(x => x.StartsStream) is { } starting
                ? Name(starting.Aggregate)
                : overridden.Count > 0 && slice.StartsStream == overridden[0] ? overridden[0] : null;

            slice.Aggregates.Clear();
            slice.StartsStream = startsOverridden;
            foreach (var aggregate in overridden.Distinct()) add(slice, aggregate);

            var said = overrides.Select(x => x.StartsStream ? $"starts {Name(x.Aggregate)}" : Name(x.Aggregate));
            report.Add($"slice '{slice.Name}': aggregate(s) {string.Join(", ", said)} from --aggregate / --starts-stream.");
        }
        else
        {
            foreach (var aggregate in new[] { slice.StartsStream }.Concat(slice.Aggregates).OfType<string>().Distinct())
            {
                var stream = streams.ByName(aggregate);
                if (stream is null || stream.IsDeclared) continue;

                var starting = aggregate == slice.StartsStream;
                var given = givenOn(stream);
                var role = starting ? $"starts the {aggregate} stream" : $"decides against {aggregate}";
                var why = starting
                    ? $"no example gives it an earlier {aggregate} event; {stream.Why}"
                    : written.Contains(stream)
                        ? $"its examples give {aggregate} events before it appends; {stream.Why}"
                        : $"its examples give {string.Join(", ", given)} from the {aggregate} stream; {stream.Why}";

                slice.Inferred[aggregate] = why;
                report.Add($"⚠ slice '{slice.Name}': inferred that it {role} — {why}.");

                // bobcat#454: the only evidence that the stream already exists is events nothing
                // imported emits, so the slice may well be what starts it
                if (!starting && given.Count > 0 && given.All(x => !emitted.Contains(x)))
                {
                    var chapters = given.Select(x => unimported.GetValueOrDefault(x)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                    var source = chapters.Count > 0
                        ? $"{(chapters.Count == 1 ? "chapter" : "chapters")} {string.Join(", ", chapters.Select(x => $"'{x}'"))}, which {(chapters.Count == 1 ? "was" : "were")} not imported"
                        : "no imported slice";
                    var question = $"⚠ starts the {aggregate} stream? It is inferred to decide against {aggregate}, but the only {aggregate} "
                                   + $"events its examples give first, {string.Join(", ", given)}, come from {source}. "
                                   + $"If it starts the stream, import with --starts-stream {slice.Name}={aggregate}.";
                    context.Add(new ImportCallout(slice.Name, ImportCalloutCodes.StreamStartFromUnimportedChapter, [aggregate], question,
                        $"--starts-stream {slice.Name}={aggregate}")
                    {
                        Events = given, Examples = examplesGiving(stream), Chapters = chapters
                    });
                    report.Add($"⚠ slice '{slice.Name}' {question[2..]}");
                    continue;
                }

                context.Add(starting
                    ? new ImportCallout(slice.Name, ImportCalloutCodes.InferredStreamStart, [aggregate], $"⚠ inferred: {role} — {why}.",
                        $"--starts-stream {slice.Name}={aggregate}")
                    {
                        Events = eventsOf(stream), Examples = scenarios.Select(x => x.Name).ToList()
                    }
                    : new ImportCallout(slice.Name, ImportCalloutCodes.InferredAgainst, [aggregate], $"⚠ inferred: {role} — {why}.",
                        $"--aggregate {slice.Name}={aggregate}")
                    {
                        Events = given, Examples = examplesGiving(stream)
                    });
            }
        }

        var all = new[] { slice.StartsStream }.Concat(slice.Aggregates).OfType<string>().Distinct().ToList();
        if (all.Count > 1)
        {
            var callout = string.Format(MultiStreamCallout, string.Join(", ", all));
            context.Add(new ImportCallout(slice.Name, ImportCalloutCodes.SeveralStreams, all, callout, null)
            {
                Examples = scenarios.Select(x => x.Name).ToList()
            });
            report.Add($"⚠ slice '{slice.Name}' {callout[2..]}");
        }

        if (all.Count == 0)
        {
            const string missing = "⚠ missing: no aggregate. TODO: a command that does not only start a stream needs a DCB decider or one or more single-stream aggregates — declare .Against<T>(), or import with --aggregate Slice=Type.";
            context.Add(new ImportCallout(slice.Name, ImportCalloutCodes.MissingAggregate, [], missing, $"--aggregate {slice.Name}=<Type>"));
            report.Add($"⚠ slice '{slice.Name}': {missing[2..]}");
        }
    }

    private static void add(CuratedSlice slice, string aggregate)
    {
        if (aggregate != slice.StartsStream && !slice.Aggregates.Contains(aggregate)) slice.Aggregates.Add(aggregate);
    }

    /// <summary>A board name as the type name the stubs give it.</summary>
    internal static string Name(string label) => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(label ?? ""));

    private static readonly Regex Word = new("[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

    /// <summary>An event's words, minus the last: <c>AppointmentNoShowRecorded</c> → Appointment, No, Show.</summary>
    public static IReadOnlyList<string> SubjectOf(string eventName)
    {
        var words = Word.Matches(eventName).Select(x => x.Value).ToList();
        return words.Count > 1 ? words[..^1] : [];
    }

    /// <summary>Whether two events' subjects share a word, which is what lets an example join them into one stream.</summary>
    public static bool SharesSubject(string left, string right)
        => SubjectOf(left).Intersect(SubjectOf(right), StringComparer.Ordinal).Any();

    internal sealed class Stream
    {
        public List<string> Events { get; } = [];
        public string? Pinned { get; set; }
        public string? DeclaredBy { get; set; }
        public bool Linked { get; set; }
        public string? Name { get; set; }
        public string Why { get; set; } = "";
        public List<string> JoinedByName { get; } = [];
        public bool IsDeclared => Pinned is not null;
    }

    internal sealed class Streams
    {
        private readonly Dictionary<string, Stream> _byEvent = new(StringComparer.Ordinal);
        private readonly List<Stream> _all = [];

        public void Add(string name)
        {
            if (name.Length == 0 || _byEvent.ContainsKey(name)) return;
            var stream = new Stream();
            stream.Events.Add(name);
            _byEvent[name] = stream;
            _all.Add(stream);
        }

        public Stream? StreamOf(string name) => _byEvent.GetValueOrDefault(name);

        public Stream? ByName(string name) => _all.FirstOrDefault(x => x.Name == name);

        public void Pin(string name, string stream, string declaredBy)
        {
            Add(name);
            if (stream.Length == 0 || _byEvent.GetValueOrDefault(name) is not { } found) return;
            if (found.Pinned is not null && declaredBy.StartsWith("the model", StringComparison.Ordinal)) return;
            found.Pinned = stream;
            found.DeclaredBy = declaredBy;
        }

        public void Join(string left, string right, bool lineage)
        {
            Add(left);
            Add(right);
            if (_byEvent.GetValueOrDefault(left) is not { } a || _byEvent.GetValueOrDefault(right) is not { } b) return;
            if (a == b)
            {
                a.Linked |= lineage;
                return;
            }

            // Two declarations of different streams are never one stream
            if (a.Pinned is not null && b.Pinned is not null && a.Pinned != b.Pinned) return;

            merge(into: a, from: b);
            a.Linked |= lineage;
        }

        private void merge(Stream into, Stream from)
        {
            foreach (var name in from.Events)
            {
                if (!into.Events.Contains(name)) into.Events.Add(name);
                _byEvent[name] = into;
            }

            into.Pinned ??= from.Pinned;
            into.DeclaredBy ??= from.DeclaredBy;
            into.Linked |= from.Linked;
            _all.Remove(from);
        }

        public void NameAndMerge(List<string> report)
        {
            foreach (var stream in _all)
            {
                if (stream.Pinned is { } pinned)
                {
                    stream.Name = pinned;
                    stream.Why = stream.DeclaredBy ?? "declared";
                    continue;
                }

                (stream.Name, stream.Why) = nameOf(stream.Events);
            }

            // A stream no example links joins one its subject ends with, or one of the same name
            foreach (var stream in _all.ToList())
            {
                if (!_all.Contains(stream) || stream.IsDeclared || stream.Name is null) continue;

                var target = _all
                    .Where(x => x != stream && x.Name is not null)
                    .Where(x => x.Name == stream.Name
                                || (!stream.Linked && x.Events.Count > stream.Events.Count && endsWithWords(stream.Name, x.Name!)))
                    .OrderByDescending(x => x.Events.Count)
                    .ThenBy(x => x.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (target is null) continue;

                var joined = string.Join(", ", stream.Events);
                report.Add(target.Name == stream.Name
                    ? $"⚠ inferred: {joined} joined the {target.Name} stream, which has the same name."
                    : $"⚠ inferred: {joined} joined the {target.Name} stream by name only — '{stream.Name}' ends in '{target.Name}', and no example links it to another event.");
                if (target.Name != stream.Name) target.JoinedByName.AddRange(stream.Events);
                merge(into: target, from: stream);
            }

            foreach (var stream in _all.Where(x => x.Name is not null && !x.IsDeclared))
            {
                var joined = stream.JoinedByName.Count == 0 ? "" : $"; {string.Join(", ", stream.JoinedByName)} joined it by name only";
                report.Add($"⚠ inferred aggregate {stream.Name} for {string.Join(", ", stream.Events)}: {stream.Why}{joined}.");
            }

            foreach (var stream in _all.Where(x => x.Name is null))
            {
                report.Add($"⚠ no aggregate can be named for {string.Join(", ", stream.Events)}: a one-word event name has no subject.");
            }
        }

        public IEnumerable<(string Event, string Stream)> Assignments()
            => _byEvent.Where(x => x.Value.Name is not null).Select(x => (x.Key, x.Value.Name!));

        private static bool endsWithWords(string name, string suffix)
        {
            var words = Word.Matches(name).Select(x => x.Value).ToList();
            var tail = Word.Matches(suffix).Select(x => x.Value).ToList();
            return tail.Count > 0 && tail.Count < words.Count && words.Skip(words.Count - tail.Count).SequenceEqual(tail);
        }

        /// <summary>
        /// The longest run of words a strict majority of the stream's events open with (ties: the
        /// commonest, then alphabetical), or the one event's subject.
        /// </summary>
        private static (string? Name, string Why) nameOf(IReadOnlyList<string> events)
        {
            var subjects = events.Select(SubjectOf).Where(x => x.Count > 0).ToList();
            if (subjects.Count == 0) return (null, "");
            if (events.Count == 1) return (string.Concat(subjects[0]), $"named for the subject of {events[0]}, its only event");

            var counts = new Dictionary<string, (int Count, int Words)>(StringComparer.Ordinal);
            foreach (var subject in subjects)
            {
                var runs = new HashSet<string>(StringComparer.Ordinal);
                for (var start = 0; start < subject.Count; start++)
                for (var length = 1; start + length <= subject.Count; length++)
                    runs.Add(string.Concat(subject.Skip(start).Take(length)));

                foreach (var run in runs)
                {
                    var words = Word.Matches(run).Count;
                    counts[run] = (counts.GetValueOrDefault(run).Count + 1, words);
                }
            }

            var best = counts
                .Where(x => x.Value.Count * 2 > events.Count)
                .OrderByDescending(x => x.Value.Words)
                .ThenByDescending(x => x.Value.Count)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => (x.Key, x.Value.Count))
                .FirstOrDefault();

            if (best.Key is null)
            {
                // No subject a majority shares: the first event's, which is usually the one that starts it
                return (string.Concat(subjects[0]), $"no subject is shared by most of {string.Join(", ", events)}, so named for {events[0]}");
            }

            return (best.Key, $"'{best.Key}' is the subject {best.Count} of its {events.Count} events share");
        }
    }
}
