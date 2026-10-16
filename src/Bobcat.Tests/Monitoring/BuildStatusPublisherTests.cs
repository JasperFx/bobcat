using System.Net;
using System.Text.Json;
using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// bobcat#651: <c>bobcat watch</c> tells a console how a rebuild went, so a project that no longer
/// compiles stops reading as green on the board. On the publisher's terms: never slows or fails the
/// loop, and <c>BOBCAT_MONITOR=0</c> sends nothing.
/// </summary>
[Collection("monitor-kill-switch")]
public class BuildStatusPublisherTests : IDisposable
{
    private readonly string? _killSwitch = Environment.GetEnvironmentVariable(MonitorPublisher.KillSwitchVariable);

    public BuildStatusPublisherTests() => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, null);

    public void Dispose() => Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, _killSwitch);

    private sealed class Recording(HttpStatusCode status = HttpStatusCode.Accepted, bool fail = false) : HttpMessageHandler
    {
        public List<(string Url, JsonElement Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("connection refused");
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            lock (Posts) Posts.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status);
        }
    }

    private static BuildStatusReport failed()
        => new("Bank.Tests", "/code/bank", "main", BuildStatusReport.BuildFailed, "Specs.cs(3,1): error CS1002: ; expected",
            DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task a_report_goes_to_every_console_as_camel_case_json()
    {
        var handler = new Recording();

        (await BuildStatusPublisher.PublishAsync(failed(), ["http://one:5525", "http://two:6000/"], handler)).ShouldBe(2);

        handler.Posts.Select(x => x.Url).OrderBy(x => x)
            .ShouldBe(["http://one:5525/api/build-status", "http://two:6000/api/build-status"]);
        var body = handler.Posts[0].Body;
        body.GetProperty("suite").GetString().ShouldBe("Bank.Tests");
        body.GetProperty("state").GetString().ShouldBe("build-failed");
        body.GetProperty("detail").GetString().ShouldContain("CS1002");
    }

    [Fact]
    public async Task an_absent_console_or_one_without_the_endpoint_costs_nothing()
    {
        (await BuildStatusPublisher.PublishAsync(failed(), ["http://nowhere:1"], new Recording(fail: true))).ShouldBe(0);
        (await BuildStatusPublisher.PublishAsync(failed(), ["http://stoat:5600"], new Recording(HttpStatusCode.NotFound))).ShouldBe(0);
    }

    [Fact]
    public async Task the_kill_switch_sends_nothing()
    {
        Environment.SetEnvironmentVariable(MonitorPublisher.KillSwitchVariable, "0");
        var handler = new Recording();

        (await BuildStatusPublisher.PublishAsync(failed(), ["http://one:5525"], handler)).ShouldBe(0);
        handler.Posts.ShouldBeEmpty();
    }

    [Fact]
    public void the_errors_are_the_compiler_error_lines_once_each_and_bounded()
    {
        var output = string.Join('\n',
        [
            "  Determining projects to restore...",
            "/code/bank/Specs.cs(3,1): error CS1002: ; expected [/code/bank/Bank.Tests.csproj::TargetFramework=net9.0]",
            "/code/bank/Specs.cs(3,1): error CS1002: ; expected [/code/bank/Bank.Tests.csproj::TargetFramework=net9.0]",
            "/code/bank/Specs.cs(8,5): warning CS0168: unused",
            .. Enumerable.Range(0, 20).Select(i => $"/code/bank/Other.cs({i},1): error CS0103: nope")
        ]);

        var errors = BuildStatusPublisher.ErrorsIn(output).Split(Environment.NewLine);

        errors.Length.ShouldBe(BuildStatusPublisher.ErrorLines);
        errors[0].ShouldContain("CS1002");
        errors.Count(x => x.Contains("CS1002")).ShouldBe(1);
        errors.ShouldNotContain(x => x.Contains("warning"));
    }

    [Fact]
    public void a_failure_with_no_line_that_reads_as_an_error_still_reports_its_last_words()
    {
        BuildStatusPublisher.ErrorsIn("restore failed\nthe SDK was not found").ShouldContain("the SDK was not found");
    }
}
