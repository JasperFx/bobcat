using System.Runtime.CompilerServices;

namespace Bobcat.Ui.Runners;

/// <summary>A runner as Stoat knows it: what it registered, and how it stands right now.</summary>
public record RunnerView(
    string RunnerId,
    string Repository,
    string? RepositorySlug,
    string? Branch,
    string? Suite,
    string Lane,
    IReadOnlyList<string> Modes,
    int Specs,
    bool Connected,
    string? Status,
    string? StatusDetail,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastSeenAt,
    IReadOnlyList<RunnerCommandView> Commands);

/// <summary>
/// One specification a runner can run (bobcat#644): its identity split into feature and scenario,
/// and its planned steps when the runner registered them — empty for a runner that predates the
/// outline, or a data row whose steps are its method's.
/// </summary>
public record RunnerSpecView(string Identity, string Feature, string Scenario, IReadOnlyList<string> Steps);

/// <summary>One command and where it got to. <c>State</c> is queued / sent / accepted / rejected.
/// <c>RunId</c> is the run it produced, once that run's <c>run_started</c> names it.</summary>
public record RunnerCommandView(
    Guid CommandId, string Kind, IReadOnlyList<string> Specs, string Mode, string State, string? Reason,
    string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? AcknowledgedAt, Guid? RunId);

/// <summary>
/// The resident runners and their command queues (stoat#77), in memory: a runner is a live process
/// that re-registers on every start, so a Stoat restart loses nothing a runner will not tell it again
/// within seconds. Queued commands are lost with it, which is acceptable for a button a person can
/// press again. Whether commands should become events, for a "who ran what" history, is still open.
/// </summary>
/// <remarks>
/// <para>
/// <b>An open stream is a live runner.</b> No lease: <see cref="Stream"/> counts itself in and out.
/// </para>
/// <para>
/// <b>Stoat owns the queue, because the runner refuses to</b> (JasperFx/bobcat#390, as built in
/// Bobcat 0.29.0): one command at a time, and a second is refused as busy rather than queued. So a
/// runner is sent ONE command and the next waits until that one is acknowledged — and a command
/// refused as busy goes back in the queue and is sent again shortly, because a run's
/// <c>run_finished</c> arrives a hair before the runner is free and the right answer to that is to
/// send again. Sending everything at once, as this first did, would have had every second press
/// refused.
/// </para>
/// <para>
/// <b>A command is pending until it is acknowledged</b>, because an event written to a stream that
/// drops in the same instant is simply lost. A new stream re-delivers the one in flight; a
/// reconnect's <c>Last-Event-ID</c> stops it repeating what it already received. ⚠️ An acceptance
/// is final: a runner refuses a resent command it already accepted as busy, and that later answer
/// must not turn a running command into a rejected one.
/// </para>
/// <para>
/// <b>A new registration from the same checkout and suite supersedes a runner that is gone</b>, and
/// takes over its queue and its history. Bobcat 0.29.0 gives a resident runner a fresh id on every
/// start (JasperFx/bobcat#397 asks it to honour <c>BOBCAT_RUNNER_ID</c>), so without this a command
/// pressed while a runner was rebuilding would wait for an id that never comes back, and the picker
/// would fill with dead runners. The old id stays an alias, so a
/// button following a command under it still finds it.
/// </para>
/// </remarks>
public sealed class RunnerRegistry
{
    private const int RecentCommands = 20;

    /// <summary>How long to wait before sending a command again after the runner said it was busy.</summary>
    public static readonly TimeSpan DefaultBusyRetry = TimeSpan.FromSeconds(2);

    /// <summary>How long a command keeps waiting its turn behind a busy runner before it is given up on.</summary>
    public static readonly TimeSpan BusyGiveUp = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly TimeSpan _busyRetry;
    private long _sequence;

    public RunnerRegistry(TimeProvider? time = null, TimeSpan? busyRetry = null)
    {
        _time = time ?? TimeProvider.System;
        _busyRetry = busyRetry ?? DefaultBusyRetry;
    }

    private sealed class Runner(RunnerRegistered registration, string? slug, DateTimeOffset at)
    {
        public RunnerRegistered Registration { get; set; } = registration;
        public string? Slug { get; set; } = slug;
        public DateTimeOffset RegisteredAt { get; set; } = at;
        public DateTimeOffset LastSeenAt { get; set; } = at;
        public int Streams { get; set; }
        public string? Status { get; set; }
        public string? StatusDetail { get; set; }
        public List<Command> Commands { get; } = new();
        public TaskCompletionSource Changed { get; set; } = newSignal();
    }

    private sealed class Command
    {
        public required Guid Id { get; init; }
        public required long Sequence { get; set; }
        public required string Kind { get; init; }
        public required IReadOnlyList<string> Specs { get; init; }
        public required string Mode { get; init; }
        public required string RequestedBy { get; init; }
        public required DateTimeOffset RequestedAt { get; init; }
        public string State { get; set; } = "queued";
        public string? Reason { get; set; }
        public DateTimeOffset? AcknowledgedAt { get; set; }

        /// <summary>Not to be sent before this — set when the runner said it was busy.</summary>
        public DateTimeOffset? NotBefore { get; set; }

        public DateTimeOffset? FirstSentAt { get; set; }
        public bool Pending => State is "queued" or "sent";
    }

    private static TaskCompletionSource newSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Exists(string runnerId)
    {
        lock (_gate) return find(runnerId) is not null;
    }

    private Runner? find(string runnerId)
    {
        if (_runners.TryGetValue(runnerId, out var runner)) return runner;

        // An alias chain is at most as long as the number of restarts, and each hop is one lookup.
        var id = runnerId;
        var hops = 0;
        while (_aliases.TryGetValue(id, out var next) && hops++ < 1000)
        {
            if (_runners.TryGetValue(next, out runner)) return runner;
            id = next;
        }

        return null;
    }

    /// <summary>Register, or re-register: the registration is replaced, the queue kept. A runner
    /// that registers has started (again), so any "rebuilding" status is over.</summary>
    public void Register(RunnerRegistered registration, string? repositorySlug)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_runners.TryGetValue(registration.RunnerId, out var runner))
            {
                runner = new Runner(registration, repositorySlug, now);
                _runners[registration.RunnerId] = runner;
                _aliases.Remove(registration.RunnerId);
            }

            runner.Registration = registration;
            runner.Slug = repositorySlug;
            runner.RegisteredAt = now;
            runner.LastSeenAt = now;
            runner.Status = null;
            runner.StatusDetail = null;

            supersede(runner);
            wake(runner);
        }
    }

    private void supersede(Runner successor)
    {
        var gone = _runners.Values
            .Where(x => !ReferenceEquals(x, successor) && x.Streams == 0 && sameCheckout(x.Registration, successor.Registration))
            .ToList();

        foreach (var old in gone)
        {
            // A command the old process received and never answered died with it: send it again.
            foreach (var command in old.Commands.Where(c => c.State == "sent"))
            {
                command.State = "queued";
                command.Sequence = ++_sequence;
            }

            successor.Commands.AddRange(old.Commands);
            successor.Commands.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            _runners.Remove(old.Registration.RunnerId);
            _aliases[old.Registration.RunnerId] = successor.Registration.RunnerId;
            wake(old);
        }
    }

    private static bool sameCheckout(RunnerRegistered a, RunnerRegistered b)
        => string.Equals(normalize(a.Repository), normalize(b.Repository), StringComparison.Ordinal)
           && string.Equals(a.Suite, b.Suite, StringComparison.Ordinal);

    private static string normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary><c>stoat runner</c>'s word on a runner it is rebuilding, or whose build failed.</summary>
    public bool ReportStatus(string runnerId, string state, string? detail)
    {
        lock (_gate)
        {
            var runner = find(runnerId);
            if (runner is null) return false;
            runner.Status = state;
            runner.StatusDetail = detail;
            runner.LastSeenAt = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>Queue a command; it is sent as soon as the runner has nothing else in flight.</summary>
    public Guid Enqueue(string runnerId, string kind, IReadOnlyList<string> specs, string mode, string requestedBy)
    {
        lock (_gate)
        {
            var runner = find(runnerId) ?? throw new InvalidOperationException($"no runner '{runnerId}'");

            var command = new Command
            {
                Id = Guid.NewGuid(),
                Sequence = ++_sequence,
                Kind = kind,
                Specs = specs,
                Mode = mode,
                RequestedBy = requestedBy,
                RequestedAt = _time.GetUtcNow(),
            };
            runner.Commands.Add(command);

            // Keep the pending ones, and only the latest few that are settled.
            var settled = runner.Commands.Where(x => !x.Pending).ToList();
            foreach (var old in settled.Take(Math.Max(0, settled.Count - RecentCommands))) runner.Commands.Remove(old);

            wake(runner);
            return command.Id;
        }
    }

    /// <summary>
    /// The runner took the command, or refused it. False for a command Stoat never sent it. A refusal
    /// because the runner is busy puts the command back in the queue rather than settling it, and an
    /// acceptance is never undone by a refusal that arrives after it.
    /// </summary>
    public bool Acknowledge(string runnerId, Guid commandId, bool accepted, string? reason, string? refusal = null)
    {
        lock (_gate)
        {
            var runner = find(runnerId);
            var command = runner?.Commands.FirstOrDefault(x => x.Id == commandId);
            if (runner is null || command is null) return false;

            var now = _time.GetUtcNow();
            runner.LastSeenAt = now;

            if (command.State == "accepted") return true;

            if (!accepted && IsBusy(refusal, reason) && command.Kind == "run" && now - (command.FirstSentAt ?? now) < BusyGiveUp)
            {
                command.State = "queued";
                command.Reason = reason;
                command.NotBefore = now + _busyRetry;
                command.Sequence = ++_sequence;
                wake(runner);
                return true;
            }

            command.State = accepted ? "accepted" : "rejected";
            command.Reason = accepted ? null : reason;
            command.AcknowledgedAt = now;
            command.NotBefore = null;
            wake(runner);
            return true;
        }
    }

    /// <summary>
    /// Whether a refusal means "not now" rather than "not this": the refusal kind Bobcat 0.30.0 sends
    /// (bobcat#400) when there is one, and only for a runner that predates it, the reason text.
    /// </summary>
    public static bool IsBusy(string? refusal, string? reason)
        => refusal is not null
            ? refusal == "busy"
            : reason is not null
              && (reason.Contains("already running", StringComparison.OrdinalIgnoreCase)
                  || reason.Contains("busy", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The runner's command stream, as long as it stays open: the command in flight (if this stream
    /// has not delivered it) or else the next one due, as its wire message (<see cref="RunSpecsCommand"/>
    /// or <see cref="RestartRunnerCommand"/>), then a wait. A null item is a keep-alive, sent when nothing
    /// has happened for <paramref name="keepAlive"/>. The runner counts as connected while this
    /// enumerates.
    /// </summary>
    /// <param name="lastEventId">The SSE <c>Last-Event-ID</c> a reconnecting runner sent: the last
    /// command it received, and so where this stream resumes. Null or unknown starts from the top of
    /// what is still pending.</param>
    public async IAsyncEnumerable<object?> Stream(
        string runnerId, Guid? lastEventId, TimeSpan keepAlive, [EnumeratorCancellation] CancellationToken ct)
    {
        long sent;
        string id;
        lock (_gate)
        {
            var runner = find(runnerId);
            if (runner is null) yield break;
            id = runner.Registration.RunnerId;
            runner.Streams++;
            runner.LastSeenAt = _time.GetUtcNow();
            sent = lastEventId is { } last && runner.Commands.FirstOrDefault(x => x.Id == last) is { } known ? known.Sequence : 0;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                object? due = null;
                Task changed;
                var wait = keepAlive;
                lock (_gate)
                {
                    if (find(id) is not { } runner) yield break;
                    id = runner.Registration.RunnerId;

                    var now = _time.GetUtcNow();
                    var next = nextFor(runner, now);
                    if (next is not null && next.Sequence > sent)
                    {
                        next.State = "sent";
                        next.FirstSentAt ??= now;
                        sent = next.Sequence;
                        due = wire(next);
                    }
                    else if (next is null && soonestRetry(runner, now) is { } soonest && soonest < wait)
                    {
                        // A busy retry comes due before the keep-alive would: wake for it instead.
                        wait = soonest;
                    }

                    changed = runner.Changed.Task;
                }

                if (due is not null)
                {
                    yield return due;
                    continue;
                }

                var finished = await Task.WhenAny(changed, Task.Delay(wait, _time, ct)).ConfigureAwait(false);
                if (ct.IsCancellationRequested) yield break;
                if (finished != changed && wait == keepAlive) yield return null;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (find(id) is { } runner) runner.Streams--;
            }
        }
    }

    // One at a time: the command in flight while there is one, else the oldest one due.
    private static Command? nextFor(Runner runner, DateTimeOffset now)
        => runner.Commands.FirstOrDefault(c => c.State == "sent")
           ?? runner.Commands.Where(c => c.State == "queued" && (c.NotBefore is null || c.NotBefore <= now))
               .OrderBy(c => c.Sequence)
               .FirstOrDefault();

    private static TimeSpan? soonestRetry(Runner runner, DateTimeOffset now)
    {
        var waiting = runner.Commands.Where(c => c.State == "queued" && c.NotBefore > now).Select(c => c.NotBefore!.Value - now).ToList();
        return waiting.Count == 0 ? null : waiting.Min();
    }

    public IReadOnlyList<RunnerView> All(Func<Guid, Guid?>? runFor = null)
    {
        lock (_gate)
        {
            return _runners.Values
                .OrderByDescending(x => x.Streams > 0)
                .ThenBy(x => x.Registration.RunnerId, StringComparer.Ordinal)
                .Select(x => view(x, runFor))
                .ToList();
        }
    }

    public RunnerView? Find(string runnerId, Func<Guid, Guid?>? runFor = null)
    {
        lock (_gate) return find(runnerId) is { } runner ? view(runner, runFor) : null;
    }

    /// <summary>
    /// The runners that can run these specs (stoat#78's picker): every one of them registered, or —
    /// for a feature — at least one spec in it. Connected first, because a runner that is listening
    /// starts at once; a registered one that is not (rebuilding, say) still takes the command, which
    /// waits in its queue.
    /// </summary>
    public IReadOnlyList<RunnerView> CandidatesFor(IReadOnlyList<string>? specs, string? feature, Func<Guid, Guid?>? runFor = null)
    {
        lock (_gate)
        {
            return _runners.Values
                .Where(x => feature is not null
                    ? x.Registration.Specs.Any(spec => spec.StartsWith(feature + "/", StringComparison.Ordinal))
                    : specs is { Count: > 0 } && specs.All(spec => x.Registration.Specs.Contains(spec, StringComparer.Ordinal)))
                .OrderByDescending(x => x.Streams > 0)
                .ThenByDescending(x => x.LastSeenAt)
                .Select(x => view(x, runFor))
                .ToList();
        }
    }

    /// <summary>
    /// What the runner can run, spec by spec with its planned steps (bobcat#644) — the runner page's
    /// list, before anything has run. Null for a runner nobody registered.
    /// </summary>
    public IReadOnlyList<RunnerSpecView>? SpecificationsOf(string runnerId)
    {
        lock (_gate)
        {
            if (find(runnerId) is not { } runner) return null;

            var steps = (runner.Registration.Outline ?? [])
                .GroupBy(x => x.Identity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Steps, StringComparer.Ordinal);

            return runner.Registration.Specs
                .Distinct(StringComparer.Ordinal)
                .Select(identity =>
                {
                    // The identity cannot be parsed in general (a feature title may hold a '/'), but a
                    // display split at the last one is what the rest of the board already does.
                    var slash = identity.LastIndexOf('/');
                    return new RunnerSpecView(
                        identity,
                        slash > 0 ? identity[..slash] : "",
                        slash > 0 ? identity[(slash + 1)..] : identity,
                        steps.TryGetValue(identity, out var planned) ? planned : []);
                })
                .OrderBy(x => x.Feature, StringComparer.Ordinal)
                .ThenBy(x => x.Scenario, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>The runner's registered spec identities — what a command may name.</summary>
    public IReadOnlyList<string>? SpecsOf(string runnerId)
    {
        lock (_gate) return find(runnerId)?.Registration.Specs;
    }

    /// <summary>The modes the runner registered; cold when it named none.</summary>
    public IReadOnlyList<string>? ModesOf(string runnerId)
    {
        lock (_gate)
        {
            return find(runnerId) is { } runner
                ? runner.Registration.Modes is { Count: > 0 } modes ? modes : [RunnerWire.Cold]
                : null;
        }
    }

    private static object wire(Command command)
        => command.Kind == "restart"
            ? new RestartRunnerCommand(command.Id)
            : new RunSpecsCommand(command.Id, command.Specs, command.Mode);

    private static void wake(Runner runner)
    {
        var previous = runner.Changed;
        runner.Changed = newSignal();
        previous.TrySetResult();
    }

    private static RunnerView view(Runner runner, Func<Guid, Guid?>? runFor)
    {
        var r = runner.Registration;
        return new RunnerView(
            r.RunnerId, r.Repository, runner.Slug, r.Branch, r.Suite, r.Lane,
            r.Modes is { Count: > 0 } modes ? modes : [RunnerWire.Cold],
            r.Specs.Count, runner.Streams > 0, runner.Status, runner.StatusDetail,
            runner.RegisteredAt, runner.LastSeenAt,
            runner.Commands.OrderByDescending(x => x.Sequence).Select(c => new RunnerCommandView(
                c.Id, c.Kind, c.Specs, c.Mode, c.State, c.Reason, c.RequestedBy, c.RequestedAt, c.AcknowledgedAt,
                runFor?.Invoke(c.Id))).ToList());
    }
}
