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
