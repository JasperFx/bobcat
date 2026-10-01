using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// When a projected run prints its specifications without being asked (issue #384). The rule is a
/// pure function of three facts, so it is read and tested here without a process, a terminal or a
/// console anywhere.
/// </summary>
public class ProjectedSpecConsoleDefaultTests
{
    private static bool wouldPrint(bool? setting, bool terminal, bool wire)
        => ProjectedSpecConsole.WouldEnableByDefault(setting, terminal, wire);

    [Fact]
    public void a_terminal_with_nothing_listening_prints()
    {
        // The case the default exists for: without it this run produces no specification anywhere,
        // which is the wrong default for a lane people adopt one class at a time.
        wouldPrint(setting: null, terminal: true, wire: false).ShouldBeTrue();
    }

    [Fact]
    public void a_console_on_the_wire_renders_it_better_so_this_stays_quiet()
    {
        wouldPrint(setting: null, terminal: true, wire: true).ShouldBeFalse();
    }

    [Fact]
    public void a_captured_stream_belongs_to_whoever_captured_it()
    {
        // dotnet test, a CI runner, a pipe. A second report interleaves with the platform's own.
        wouldPrint(setting: null, terminal: false, wire: false).ShouldBeFalse();
        wouldPrint(setting: null, terminal: false, wire: true).ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_yes_wins_over_both_conditions()
    {
        wouldPrint(setting: true, terminal: false, wire: true).ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_no_wins_over_the_default()
    {
        // The reason the setting is a tri-state. Collapsed to a bool, "somebody said no" and "nobody
        // said" are the same value, and BOBCAT_SPEC_CONSOLE=0 would have printed anyway.
        wouldPrint(setting: false, terminal: true, wire: false).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    public void a_set_variable_reads_as_what_it_says(string value, bool expected)
    {
        var variable = $"BOBCAT_TEST_SWITCH_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            ProjectedSpecConsole.Setting(variable).ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void an_unset_or_blank_variable_is_the_third_state(string? value)
    {
        var variable = $"BOBCAT_TEST_SWITCH_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            ProjectedSpecConsole.Setting(variable).ShouldBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
