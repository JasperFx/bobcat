using System.Net;
using System.Net.Sockets;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #200's second half: when a suite cannot bind a port, say who has it.
/// </summary>
public class PortHolderTests
{
    /// <summary>The shape Kestrel actually throws: the address is in the wrapper, the error in the socket.</summary>
    private static Exception bindFailure(int port)
        => new IOException($"Failed to bind to address http://127.0.0.1:{port}.",
            new SocketException((int)SocketError.AddressAlreadyInUse));

    [Fact]
    public void the_port_comes_from_the_wrapper_and_the_verdict_from_the_socket()
    {
        PortHolder.PortOf(bindFailure(5000)).ShouldBe(5000);
    }

    [Fact]
    public void an_unrelated_failure_is_not_a_bind_collision()
    {
        // Both halves are required. A message that happens to carry a port is not a collision...
        PortHolder.PortOf(new InvalidOperationException("could not reach http://localhost:5445")).ShouldBeNull();

        // ...and a socket error that is not AddressAlreadyInUse is not one either.
        PortHolder.PortOf(new IOException("Failed to bind to address http://127.0.0.1:5000.",
            new SocketException((int)SocketError.ConnectionRefused))).ShouldBeNull();

        PortHolder.Explain(new InvalidOperationException("the database was not there")).ShouldBe(string.Empty);
    }

    [Fact]
    public void explaining_a_collision_no_longer_happening_says_so_rather_than_guessing()
    {
        // A free port: the holder released it between the failure and the check, which is a real
        // race. Saying "could not identify" is the honest answer, and it hands over the command
        // that would have answered.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var free = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var explanation = PortHolder.Explain(bindFailure(free));

        explanation.ShouldContain($"Port {free} is already in use");
        explanation.ShouldContain($"lsof -nP -iTCP:{free}");
    }

    [Fact]
    public void a_held_port_names_the_process_holding_it()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var holder = PortHolder.Describe(port);
            if (holder is null)
            {
                Assert.Skip($"No socket-listing tool answered on this machine, so port {port}'s holder cannot be named.");
            }

            // We are the holder — this test process.
            holder.ShouldContain($"pid {Environment.ProcessId}");
            PortHolder.Explain(bindFailure(port)).ShouldContain("that process, not this suite, is what has to go");
        }
        finally
        {
            listener.Stop();
        }
    }
}
