using System.Net.Sockets;
using Bobcat.Runtime;

namespace Bobcat.Supervisor;

/// <summary>
/// A named resource a gate's unit holds while it runs — a container, a database server, a port
/// (bobcat#729, <c>design/local-push-gates.md</c>). Exclusive: units naming the same track run
/// one at a time, and tracks run concurrently with each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capacity is 1 and the declaration does not offer more.</b> Names, not containers, are what
/// make two projects collide on one server, and a scheduler cannot see names — so the
/// conservative default is "shares the container exclusively". Sharing a track is a later, opt-in
/// refinement that waits on a measurement showing two projects on one container beating one after
/// the other.
/// </para>
/// <para>
/// A track carries its own <see cref="Preflight"/>: checks that run once, before the gate builds
/// or launches anything, so a stopped container is reported by track in seconds rather than as a
/// thousand identical failures ten minutes in. A check is a delegate that throws to fail — there
/// is no Bobcat <c>IEnvironmentCheck</c>. The gate never starts, stops or resets a container; that
/// is the repository's compose target's job.
/// </para>
/// </remarks>
public sealed class Track
{
    public Track(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A track needs a name.", nameof(name));
        Name = name;
    }

    /// <summary>The name a project's <see cref="GateProject.On"/> refers to. Case-insensitive.</summary>
    public string Name { get; }

    /// <summary>The checks run once for this track before the gate launches anything.</summary>
    public Preflight Preflight { get; } = new();

    /// <summary>
    /// How to heal this track's service when a unit running on it degrades (bobcat#824), or null
    /// for a track that only reports. Set through <see cref="Heal(Func{CancellationToken,Task})"/>.
    /// </summary>
    public Func<TrackHealContext, CancellationToken, Task>? Healer { get; private set; }

    /// <summary>Whether this track declared a heal.</summary>
    public bool CanHeal => Healer is not null;

    /// <summary>
    /// How many times this track may be healed in one gate run before the next degradation aborts
    /// the unit instead. Default 2.
    /// </summary>
    /// <remarks>
    /// Two, not one, because a gate runs several units on one track one after the other and the
    /// state that degrades a service piles up across all of them: a heal early in the gate (state
    /// the service carried in from before — Wolverine's 5-day-old SQL Server) and a heal late in it
    /// (state the gate's own units added — the emulator's 50-queue limit) are both plausible in one
    /// run. A third degradation within one gate run is the service saying something a restart
    /// does not fix, and recreating it a third time would hide that behind a longer wall clock.
    /// Counted per track per gate run, not per unit, because the service is the track's.
    /// </remarks>
    public int MaxHeals { get; set; } = 2;

    /// <summary>
    /// Declare how to heal this track's service when a unit on it degrades (bobcat#824): recreate
    /// the containers, wait until they answer, and re-run whatever setup the service needs — the
    /// lane catalogs a build creates at the start of a run, which recreating the container drops.
    /// The gate re-runs this track's <see cref="Preflight"/> afterwards, so the heal need not
    /// repeat it. Throw to fail the heal, which aborts the unit naming this track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and the only opt-in.</b> A track with no heal only ever reports a degradation —
    /// the supervisor logs <c>DEGRADED:</c> and goes on, exactly as a stall is reported under
    /// <see cref="StallAction.Report"/>. A gate without tracks changes nothing at all.
    /// </para>
    /// <para>
    /// <b>A heal never touches another track's service</b>, by construction: units on one track run
    /// one at a time, a heal runs while the degraded unit is paused with nothing in flight, and the
    /// hook is the track's own. Two services one hook would recreate together belong on one track.
    /// </para>
    /// </remarks>
    public Track Heal(Func<TrackHealContext, CancellationToken, Task> heal)
    {
        Healer = heal ?? throw new ArgumentNullException(nameof(heal));
        return this;
    }

    /// <summary>
    /// <see cref="Heal(Func{TrackHealContext,CancellationToken,Task})"/> for a heal that does not
    /// need to know why it was asked.
    /// </summary>
    public Track Heal(Func<CancellationToken, Task> heal)
    {
        if (heal is null) throw new ArgumentNullException(nameof(heal));
        return Heal((_, ct) => heal(ct));
    }

    /// <summary>Add a check; throw to fail.</summary>
    public Track Check(string description, Action check)
    {
        Preflight.Add(description, check);
        return this;
    }

    /// <summary>Add an asynchronous check; throw to fail.</summary>
    public Track Check(string description, Func<CancellationToken, Task> check)
    {
        Preflight.Add(description, check);
        return this;
    }

    /// <summary>
    /// A track whose preflight is that something accepts a TCP connection at
    /// <paramref name="host"/>:<paramref name="port"/> — the cheapest honest answer to "is the
    /// container up", and the one that catches a compose project that was never started.
    /// </summary>
    /// <remarks>
    /// Deliberately not "is it OUR container": two repositories publishing Postgres on one port is
    /// real (Marten and Weasel both take 5432), but telling them apart needs the docker CLI, and
    /// a gate that depends on docker to decide whether to run is a gate that cannot run where the
    /// database is simply installed. Add a <see cref="Check(string, Action)"/> for that when a
    /// repository needs it.
    /// </remarks>
    public static Track Listening(string name, string host, int port, TimeSpan? timeout = null)
        => new Track(name).Check($"{name}: something listening at {host}:{port}", async token =>
        {
            using var client = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

            try
            {
                await client.ConnectAsync(host, port, deadline.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"Nothing answered at {host}:{port} within {(timeout ?? TimeSpan.FromSeconds(5)).TotalSeconds:0}s.");
            }
        });

    public override string ToString() => Name;
}

/// <summary>
/// What a track's heal is told (bobcat#824): the track, the unit whose run degraded, why, and
/// which heal of this track in the gate run this is.
/// </summary>
/// <param name="Heal">1-based; <see cref="Track.MaxHeals"/> bounds it.</param>
public sealed record TrackHealContext(Track Track, GateUnit Unit, RunDegradation Degradation, int Heal);
