using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#785: a usage mistake is one line on standard error, a pointer to <c>bobcat help &lt;command&gt;</c>,
/// and exit 1. JasperFx answered an unknown flag with a page of usage and an exception from a command
/// with its stack trace — <c>bobcat run --repeat 5</c> hands <c>5</c> to the project argument, and the
/// "no project at '5'" was a stack trace.
/// </summary>
public class UsageErrorTests
{
    private static async Task<string> oneLineRefusal(string command, params string[] arguments)
    {
        var result = await Tool.Run(arguments);

        result.ExitCode.ShouldBe(1, result.Output + result.Error);
        result.Output.ShouldBeEmpty();

        var lines = Tool.Lines(result.Error);
        lines.Length.ShouldBe(1, result.Error);
        lines[0].ShouldStartWith($"bobcat {command}: ");
        lines[0].ShouldEndWith($"See 'bobcat help {command}'.");
        lines[0].ShouldNotContain("   at ");

        return lines[0];
    }

    [Fact]
    public async Task an_unknown_flag()
    {
        var line = await oneLineRefusal("run", "run", "--no-such-flag");
        line.ShouldBe("bobcat run: Unknown argument or flag for value --no-such-flag. See 'bobcat help run'.");
    }

    [Fact]
    public async Task a_short_flag_that_went_long_only_is_unknown()
    {
        var line = await oneLineRefusal("run", "run", "-f", "net10.0");
        line.ShouldContain("Unknown argument or flag for value -f");
    }

    [Fact]
    public async Task a_value_where_none_is_expected_reaches_the_project_argument_and_is_refused_by_the_command()
    {
        // --repeat takes no value, so JasperFx binds 5 to the project argument; the command sees it
        var line = await oneLineRefusal("run", "run", "--repeat", "5");
        line.ShouldContain("There is no project or directory at '5'");
        line.ShouldContain("'bobcat help' lists them");
    }

    [Fact]
    public async Task a_value_where_none_is_expected_on_a_command_without_arguments()
    {
        var line = await oneLineRefusal("ui", "ui", "5");
        line.ShouldContain("Unknown argument or flag for value 5");
    }

    [Fact]
    public async Task a_value_of_the_wrong_type()
    {
        var line = await oneLineRefusal("run", "run", "--retries", "abc");
        line.ShouldContain("Could not read the arguments");
        line.ShouldContain("'abc'");
    }

    [Fact]
    public async Task a_missing_argument()
    {
        var line = await oneLineRefusal("resident", "resident");
        line.ShouldBe("bobcat resident: Invalid usage. See 'bobcat help resident'.");
    }

    [Fact]
    public async Task a_word_that_is_neither_a_command_nor_a_project()
    {
        // A bare `bobcat x` is `bobcat run x`, so a mistyped command arrives as a project path
        var line = await oneLineRefusal("run", "nonsense-command");
        line.ShouldContain("There is no project or directory at 'nonsense-command'");
    }

    [Fact]
    public async Task help_for_a_command_shows_the_flags_as_the_tool_takes_them()
    {
        var result = await Tool.Run("help", "run");

        result.Output.ShouldContain("--framework");
        result.Output.ShouldNotContain("-f, --framework");
        result.Output.ShouldContain("-w, --workers");

        // JasperFx's default app name is `dotnet run --`; the tool is `bobcat`
        result.Output.ShouldContain("bobcat run");
        result.Output.ShouldNotContain("dotnet run --");
    }
}
