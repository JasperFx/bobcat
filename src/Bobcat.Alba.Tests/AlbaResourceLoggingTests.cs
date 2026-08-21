using Alba;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// The console-logging floor <c>AlbaResource&lt;TProgram&gt;</c> puts under a hosted application
/// (issue #62 gap 11). SampleWeb ships an appsettings.json with <c>"Default": "Information"</c>,
/// which is the case <c>SetMinimumLevel(Warning)</c> would NOT have silenced — appsettings levels
/// are rules, and rules beat the minimum level — so these tests prove the floor holds against a
/// host that genuinely wants to chatter.
/// </summary>
public class AlbaResourceLoggingTests
{
    private static LoggerFilterRule[] consoleRules(IAlbaHost host)
        => host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules
            .Where(r => r.ProviderName == typeof(ConsoleLoggerProvider).FullName && r.CategoryName == null)
            .ToArray();

    [Fact]
    public void the_default_console_floor_is_warning()
    {
        new AlbaResource<SampleWeb.Program>().ConsoleLogLevel.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public void with_console_log_level_is_fluent()
    {
        var resource = new AlbaResource<SampleWeb.Program>();
        resource.WithConsoleLogLevel(LogLevel.Debug).ShouldBeSameAs(resource);
        resource.ConsoleLogLevel.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public async Task by_default_a_console_wide_warning_rule_is_registered_on_the_host()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>();
        await resource.Start();

        var rule = consoleRules(resource.AlbaHost).ShouldHaveSingleItem();
        rule.LogLevel.ShouldBe(LogLevel.Warning);
        rule.Filter.ShouldBeNull();
    }

    [Fact]
    public async Task the_floor_is_the_configured_level()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>().WithConsoleLogLevel(LogLevel.Error);
        await resource.Start();

        consoleRules(resource.AlbaHost).ShouldHaveSingleItem().LogLevel.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task null_leaves_the_applications_logging_alone()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>().WithConsoleLogLevel(null);
        await resource.Start();

        consoleRules(resource.AlbaHost).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_floor_does_not_touch_other_providers()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>();
        await resource.Start();

        // Only the console rule is Bobcat's; nothing category-less and provider-less was added,
        // so the debug provider, BobcatLoggerProvider or a user's sink still see Information.
        var options = resource.AlbaHost.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        options.Rules.Where(r => r.ProviderName == null && r.CategoryName == null && r.LogLevel == LogLevel.Warning)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task a_users_own_console_rule_in_configure_wins_over_the_default()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>(configure: builder =>
            builder.ConfigureLogging(l => l.AddFilter<ConsoleLoggerProvider>((string?)null, LogLevel.Information)));
        await resource.Start();

        // Same provider, same (null) category: the later rule wins, and the user's runs after ours.
        var rules = consoleRules(resource.AlbaHost);
        rules.Length.ShouldBe(2);
        rules.Last().LogLevel.ShouldBe(LogLevel.Information);
    }

    /// <summary>
    /// The real thing: what reaches the console. SampleWeb's appsettings asks for Information;
    /// its <c>/log/{marker}</c> endpoint logs one Information and one Warning line. Console.Out is
    /// process-wide, so the cases run in one test, sequentially, and restore it.
    /// </summary>
    [Fact]
    public async Task on_the_console_information_is_filtered_and_warnings_get_through()
    {
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await using (var resource = new AlbaResource<SampleWeb.Program>())
            {
                await resource.Start();
                await resource.AlbaHost.Scenario(s => s.Get.Url("/log/default-floor"));
                await waitForConsole(captured, "SampleWeb warning default-floor");
            }

            // The console logger is asynchronous and FIFO: the warning line having arrived, the
            // information line would have arrived before it had it been allowed through.
            captured.ToString().ShouldNotContain("SampleWeb info default-floor");

            await using (var resource = new AlbaResource<SampleWeb.Program>().WithConsoleLogLevel(null))
            {
                await resource.Start();
                await resource.AlbaHost.Scenario(s => s.Get.Url("/log/no-floor"));
                await waitForConsole(captured, "SampleWeb warning no-floor");
            }

            // With the floor removed, the application's own "Default": "Information" applies.
            captured.ToString().ShouldContain("SampleWeb info no-floor");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static async Task waitForConsole(StringWriter captured, string marker)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (captured.ToString().Contains(marker)) return;
            await Task.Delay(50);
        }

        throw new TimeoutException($"'{marker}' never reached the console. Captured:\n{captured}");
    }
}
