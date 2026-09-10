using Alba;
using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Bobcat.Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine;

namespace Bobcat.CritterStack.Tests;

// --- a DOCUMENT-backed application: no event store anywhere in this file ----------------------

public record BookShipmentRequest(string Origin, string Destination, decimal WeightKg);

public record BookShipment(string Origin, string Destination, decimal WeightKg);

public class BookShipmentHandler
{
    public static readonly List<BookShipment> Booked = [];

    public static void Handle(BookShipment command)
    {
        lock (Booked) Booked.Add(command);
    }
}

[FixtureTitle("Booking shipments")]
[IncludeGrammars(typeof(HttpGrammars))]
public class BookingShipmentsFixture : CritterStackFixture;

/// <summary>
/// Issue #271: <c>Then {message} is sent</c> after an HTTP act, on an application with <b>no event
/// store</b>.
/// </summary>
/// <remarks>
/// <para>
/// The composition was already proven by <c>WalletHttp.feature</c> — but only on Marten, where
/// every act brackets a real stream. The report came from a document-backed Wolverine app
/// (<c>Storage.Insert</c>, <c>[Entity]</c>, a revisioned document), which is an ordinary Wolverine
/// shape and arguably the majority one, and nothing here covered it. So this fixes the coverage
/// gap in the same place as the diagnostic: an act that establishes no <c>ScenarioStream</c> at
/// all still has to feed the message vocabulary.
/// </para>
/// <para>
/// The other half is the failure text. Three different things were reported as one sentence —
/// "but no command has run (or it failed)" — which reads as an accusation against the reader's
/// application and was wrong in every case but one. Worst over HTTP, where
/// <c>Then the response is 202</c> has just passed on the very call being described as not having
/// run.
/// </para>
/// </remarks>
public class HttpActComposesWithMessageAssertionsTests
{
    /// <summary>
    /// Well above <see cref="HttpGrammars"/>' 5s default, because these tests pay something a real
    /// spec does not: the tracked window here also covers Wolverine's cold start — handler
    /// discovery and dynamic codegen — for a host built inside the test.
    /// </summary>
    /// <remarks>
    /// At the default this flaked on CI, roughly one run in ten and never locally. The act timed
    /// out, so the assertion under test reported "the act failed before its tracked session
    /// completed" instead of the message the test was checking — a confusing mismatch rather than
    /// an honest "this timed out". The run that caught it took <b>7.98s</b> for a test whose work
    /// is one POST. Raising it is not hiding latency: the subject of these tests is the assertion
    /// message, and 5s is a product default for a warm host, not a claim about a cold one.
    /// </remarks>
    private const int TrackedActTimeoutMs = 60_000;

    /// <summary>
    /// Fail with the act's own error rather than with a downstream mismatch.
    /// </summary>
    /// <remarks>
    /// The act captures instead of throwing, which is the whole point of the vocabulary — so a
    /// test asserting on what the act PRODUCED has to check the act ran first, or a timeout shows
    /// up as a confusing assertion about message text. That is exactly how this flake presented.
    /// </remarks>
    private static void theActSucceeded(CritterStackFixture fixture)
    {
        var error = fixture.LastExecution.Error;
        if (error != null)
            throw new Xunit.Sdk.XunitException(
                $"The tracked act did not complete, so nothing downstream is being tested: " +
                $"{error.GetType().Name}: {error.Message}");
    }

    private static AlbaResource hostResource()
        => new(async () =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<BookShipmentHandler>();
            });
            return await AlbaHost.For(builder, app =>
            {
                // The shape the report describes: the endpoint accepts, and cascades the command.
                app.MapPost("/shipments", async (BookShipmentRequest request, IMessageBus bus) =>
                {
                    await bus.PublishAsync(
                        new BookShipment(request.Origin, request.Destination, request.WeightKg));
                    return Results.Accepted();
                });
            });
        });

    [Fact]
    public async Task an_http_act_feeds_then_message_is_sent_with_no_event_store_in_sight()
    {
        lock (BookShipmentHandler.Booked) BookShipmentHandler.Booked.Clear();

        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var fixture = new BookingShipmentsFixture();
        var http = new HttpGrammars(timeoutInMilliseconds: TrackedActTimeoutMs);
        var context = new SpecExecutionContext("Booking a shipment", suite: suite)
        {
            Cancellation = CancellationToken.None,
        };

        // One context, two grammar instances — exactly what the generator emits for a fixture
        // carrying [IncludeGrammars]. They share nothing else.
        fixture.Context = context;
        http.Context = context;

        await resource.BeginScenarioScope();
        try
        {
            var table = new StepTable(
                ["Origin", "Destination", "WeightKg"],
                [["Dallas", "Austin", "12.5"]]);

            await http.WhenCommandIsPosted(typeof(BookShipmentRequest), "/shipments", table);

            theActSucceeded(fixture);
            http.ThenTheResponseIs(202);

            // The step that reported "no command has run (or it failed)".
            Should.NotThrow(() => fixture.ThenMessageIsSent(typeof(BookShipment)));
        }
        finally
        {
            await resource.EndScenarioScope();
        }

        BookShipmentHandler.Booked.Count.ShouldBe(1);
        BookShipmentHandler.Booked[0].Origin.ShouldBe("Dallas");
    }

    [Fact]
    public async Task a_message_that_was_not_sent_still_fails_on_what_was_sent()
    {
        await using var resource = hostResource();
        await resource.Start();

        var suite = new TestSuite();
        suite.AddResource(resource);

        var fixture = new BookingShipmentsFixture();
        var http = new HttpGrammars(timeoutInMilliseconds: TrackedActTimeoutMs);
        var context = new SpecExecutionContext("Booking a shipment", suite: suite)
        {
            Cancellation = CancellationToken.None,
        };
        fixture.Context = context;
        http.Context = context;

        await resource.BeginScenarioScope();
        try
        {
            var table = new StepTable(
                ["Origin", "Destination", "WeightKg"],
                [["Dallas", "Austin", "12.5"]]);

            await http.WhenCommandIsPosted(typeof(BookShipmentRequest), "/shipments", table);

            theActSucceeded(fixture);

            var ex = Should.Throw<SpecAssertionException>(
                () => fixture.ThenMessageIsSent(typeof(BookShipmentRequest)));

            // A real assertion failure listing what WAS sent — never the "nothing ran" message.
            ex.Message.ShouldContain("Sent:");
            ex.Message.ShouldContain(nameof(BookShipment));
            ex.Message.ShouldNotContain("no act has run");
        }
        finally
        {
            await resource.EndScenarioScope();
        }
    }

    // --- the diagnostic, per state ------------------------------------------------------------

    [Fact]
    public void nothing_acted_names_both_act_vocabularies()
    {
        var fixture = new BookingShipmentsFixture();

        var ex = Should.Throw<SpecAssertionException>(
            () => fixture.ThenMessageIsSent(typeof(BookShipment)));

        ex.Message.ShouldContain("no act has run in this scenario");
        // Naming only the bus act is what made a reader with an HTTP act read this as a
        // composition bug rather than as the missing When it is.
        ex.Message.ShouldContain("is received");
        ex.Message.ShouldContain("is posted to");
        ex.Message.ShouldNotContain("no command has run");
    }

    [Fact]
    public void an_act_that_threw_reports_the_exception_it_already_captured()
    {
        var fixture = new BookingShipmentsFixture();
        fixture.RecordExecution(
            new TrackedExecution(null, [], new TimeoutException("the tracked session gave up")));

        var ex = Should.Throw<SpecAssertionException>(
            () => fixture.ThenMessageIsSent(typeof(BookShipment)));

        ex.Message.ShouldContain(nameof(TimeoutException));
        ex.Message.ShouldContain("the tracked session gave up");
        // The old text blamed the application for something it had the answer to all along.
        ex.Message.ShouldNotContain("no command has run");
    }

    [Fact]
    public void a_failed_act_whose_http_call_succeeded_says_so()
    {
        var fixture = new BookingShipmentsFixture();
        IStepContext context = new SpecExecutionContext("Booking a shipment")
        {
            Cancellation = CancellationToken.None,
        };
        fixture.Context = context;

        context.SetState(new HttpExchange(
            new SpecHttpRequest("POST", "/shipments", null),
            new SpecHttpResponse(202, "")));
        fixture.RecordExecution(
            new TrackedExecution(null, [], new TimeoutException("the tracked session gave up")));

        var ex = Should.Throw<SpecAssertionException>(
            () => fixture.ThenMessageIsSent(typeof(BookShipment)));

        // This is the sentence the report needed: the endpoint DID run, and the failure is in
        // what it caused. Without it the reader is told nothing ran, one line after asserting 202.
        ex.Message.ShouldContain("The HTTP call itself completed");
        ex.Message.ShouldContain("POST");
        ex.Message.ShouldContain("/shipments");
        ex.Message.ShouldContain("202");
    }

    [Fact]
    public void the_typed_step_reports_the_same_three_states()
    {
        var fixture = new BookingShipmentsFixture();

        Should.Throw<SpecAssertionException>(() => fixture.ThenMessagesSent<BookShipment>())
            .Message.ShouldContain("no act has run in this scenario");

        fixture.RecordExecution(
            new TrackedExecution(null, [], new InvalidOperationException("boom")));

        Should.Throw<SpecAssertionException>(() => fixture.ThenMessagesSent<BookShipment>())
            .Message.ShouldContain("boom");
    }
}
