using Bobcat.Monitoring;
using JasperFx.Events.EventModeling;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// Issue #294 — the runner publishes its spec assembly's half of the Event Model when it
/// attaches to a console. These drive the policy over real HTTP against
/// <see cref="FakeMonitorHost"/>; the end-to-end proof that a REAL generated
/// <c>BobcatEventModelSource</c> reaches a REAL <c>EventModelStore</c> and comes back in the
/// merge lives in <c>Bobcat.Console.Tests.SpecEventModelPublishingTests</c>, which is the only
/// project that can see both halves of that sentence.
/// </summary>
/// <remarks>
/// Owns the <c>BOBCAT_MONITOR</c> kill switch for the same reason
/// <see cref="MonitorPublisherTests"/> does: CI sets <c>BOBCAT_MONITOR=0</c>, and without
/// clearing it these would assert against a publisher the switch refused to build.
/// </remarks>
public class SpecEventModelPublishingTests : IDisposable
{
    private readonly string? _previousKillSwitch;

    public SpecEventModelPublishingTests()
    {
        _previousKillSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);
    }

    public void Dispose()
        => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _previousKillSwitch);

    private static SpecEventModelPublisher.Half half(
        string assembly = "BankAccountES.Tests",
        string model = "BankAccountES",
        string slice = "EnrollClient",
        string specIdentity = "Bank Account Event Sourcing/Enroll a client")
        => new(
            assembly,
            SpecEventModelPublisher.SourceNameFor(assembly),
            new EventModelDescriptor(model, [
                new EventModelSliceDescriptor(slice, null, null, null, null, [], [], [])
                {
                    Specifications = [new SpecificationDescriptor(specIdentity)]
                }
            ]));

    [Fact]
    public async Task the_half_is_published_under_the_spec_assemblys_own_source()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var published = await SpecEventModelPublisher.PublishAll(publisher, [half()]);

        published.ShouldBe(["BankAccountES-Tests"]);

        var push = host.EventModelPushes.ShouldHaveSingleItem();
        push.Source.ShouldBe("BankAccountES-Tests");
        push.Body.ShouldContain("\"name\":\"BankAccountES\"");
        push.Body.ShouldContain("\"identity\":\"Bank Account Event Sourcing/Enroll a client\"");
    }

    /// <summary>
    /// The source name is the assembly's, with everything a file name will not take turned into
    /// '-'. Verbatim, every push from a real spec assembly would be a 400 — <c>EventModelStore</c>
    /// refuses '.' because the source becomes <c>event-model.{source}.json</c> — and a silent one.
    /// </summary>
    [Theory]
    [InlineData("BankAccountES.Tests", "BankAccountES-Tests")]
    [InlineData("Wallet", "Wallet")]
    [InlineData("My_Specs.v2", "My_Specs-v2")]
    [InlineData("...", "specs")]
    public void the_source_name_is_a_file_name_the_console_will_take(string assembly, string expected)
        => SpecEventModelPublisher.SourceNameFor(assembly).ShouldBe(expected);

    [Fact]
    public async Task a_re_run_replaces_its_own_source_rather_than_accumulating()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        await SpecEventModelPublisher.PublishAll(publisher, [half()]);
        await SpecEventModelPublisher.PublishAll(publisher, [half()]);

        // Idempotence is the console's to enforce per source (#268); what the runner owes is a
        // STABLE source name, so the second run addresses the first one's contribution.
        host.EventModelPushes.Select(p => p.Source).ShouldBe(["BankAccountES-Tests", "BankAccountES-Tests"]);
    }

    /// <summary>
    /// The rule issue #294 says nothing checks. <c>GET /api/event-model</c> merges only the
    /// sources naming the CURRENT model, and the current name is whatever was pushed last — so
    /// pushing a differently-named half would hide the other half instead of joining it.
    /// </summary>
    [Fact]
    public async Task a_half_naming_a_different_model_is_refused_rather_than_hiding_the_other_half()
    {
        using var host = new FakeMonitorHost { CurrentEventModelName = "BankAccountES" };
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var notices = new List<string>();
        var published = await SpecEventModelPublisher.PublishAll(
            publisher, [half(model: "BankAccountES.Tests")], notices.Add);

        published.ShouldBeEmpty();
        host.EventModelPushes.ShouldBeEmpty();

        var notice = notices.ShouldHaveSingleItem();
        notice.ShouldContain("BankAccountES.Tests");
        notice.ShouldContain("EventModelName");
    }

    [Fact]
    public async Task a_half_naming_the_model_already_on_the_console_is_published()
    {
        using var host = new FakeMonitorHost { CurrentEventModelName = "BankAccountES" };
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var notices = new List<string>();
        var published = await SpecEventModelPublisher.PublishAll(publisher, [half()], notices.Add);

        published.ShouldBe(["BankAccountES-Tests"]);
        notices.ShouldBeEmpty();
    }

    /// <summary>
    /// A console that has never been published to answers the GET with a 404, which is not a
    /// name to collide with — the first half through the door names the model.
    /// </summary>
    [Fact]
    public async Task an_empty_console_takes_the_half_and_lets_it_name_the_model()
    {
        using var host = new FakeMonitorHost();
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        (await publisher.CurrentEventModelName(CancellationToken.None)).ShouldBeNull();
        (await SpecEventModelPublisher.PublishAll(publisher, [half()])).ShouldNotBeEmpty();
    }

    /// <summary>
    /// A refusal is the one outcome a human can act on, so it is said out loud — and it is still
    /// only ever said, never thrown.
    /// </summary>
    [Fact]
    public async Task a_console_that_refuses_the_push_is_reported_and_nothing_more()
    {
        using var host = new FakeMonitorHost { RejectEventModelPushes = true };
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        var notices = new List<string>();
        var published = await SpecEventModelPublisher.PublishAll(publisher, [half()], notices.Add);

        published.ShouldBeEmpty();
        notices.ShouldHaveSingleItem().ShouldContain("400");
    }

    /// <summary>
    /// The invariant that outranks everything in <see cref="MonitorPublisher"/>: a test run is
    /// never slowed or failed by the monitor. A console that dies between the ping and the push
    /// is a dropped push, not an exception and not a wait.
    /// </summary>
    [Fact]
    public async Task a_console_that_dies_before_the_push_neither_throws_nor_hangs()
    {
        var host = new FakeMonitorHost();
        await using var publisher = await MonitorPublisher.TryConnect(host.Url);
        publisher.ShouldNotBeNull();

        host.Dispose();

        var started = DateTime.UtcNow;
        var published = await SpecEventModelPublisher.PublishAll(publisher, [half()]);

        published.ShouldBeEmpty();
        (DateTime.UtcNow - started).ShouldBeLessThan(SpecEventModelPublisher.Ceiling * 2);
    }

    /// <summary>
    /// Most Bobcat suites declare no Event Modeling slices at all, so the generator emits no
    /// source for them and there is simply nothing to publish — no request, no notice.
    /// </summary>
    [Fact]
    public void an_assembly_with_no_generated_source_contributes_no_half()
        => SpecEventModelPublisher.Discover([typeof(SpecEventModelPublishingTests).Assembly]).ShouldBeEmpty();
}
