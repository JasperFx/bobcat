using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#785: <c>bobcat resident</c> takes the spec project's build output folder or its
/// <c>.csproj</c> as well as the executable, and resolves the test host. Driven with <c>--list</c>,
/// which asks the host what it specifies and registers with nothing, against
/// <c>Bobcat.Supervisor.ProjectedWorker</c>.
/// </summary>
public class ResidentHostTests
{
    private const string Worker = "Bobcat.Supervisor.ProjectedWorker";

    private static string folder => Tool.BinDirectoryOf(Worker);
    private static string project => Path.Combine(Tool.SourceDirectory, Worker, Worker + ".csproj");

    private static void listsTheWorker((int ExitCode, string Output, string Error) result)
    {
        result.ExitCode.ShouldBe(0, result.Output + result.Error);
        result.Output.ShouldContain($"{Worker} (projected/xunit)");
        result.Output.ShouldContain("Basics/passes");
    }

    [Fact]
    public async Task the_executable_as_before()
        => listsTheWorker(await Tool.Run("resident", Path.Combine(folder, OperatingSystem.IsWindows() ? Worker + ".exe" : Worker), "--list"));

    [Fact]
    public async Task the_build_output_folder()
        => listsTheWorker(await Tool.Run("resident", folder, "--list"));

    [Fact]
    public async Task the_csproj_in_the_configuration_it_was_built_in()
        => listsTheWorker(await Tool.Run("resident", project, "--list", "--configuration", Tool.Configuration));

    [Fact]
    public async Task the_directory_holding_the_csproj()
        => listsTheWorker(await Tool.Run("resident", Path.GetDirectoryName(project)!, "--list", "--configuration", Tool.Configuration));

    [Fact]
    public async Task a_csproj_whose_build_is_not_there_is_refused_in_one_line()
    {
        var result = await Tool.Run("resident", project, "--list", "--configuration", "NoSuchConfiguration");

        result.ExitCode.ShouldBe(1);
        var lines = Tool.Lines(result.Error);
        lines.Length.ShouldBe(1, result.Error);
        lines[0].ShouldStartWith($"bobcat resident: {Worker} (net10.0, NoSuchConfiguration) is not built");
        lines[0].ShouldEndWith("See 'bobcat help resident'.");
    }

    [Fact]
    public async Task a_folder_with_no_host_is_refused_in_one_line()
    {
        var empty = Directory.CreateTempSubdirectory("bobcat-no-host-").FullName;
        try
        {
            var result = await Tool.Run("resident", empty, "--list");

            result.ExitCode.ShouldBe(1);
            var lines = Tool.Lines(result.Error);
            lines.Length.ShouldBe(1, result.Error);
            lines[0].ShouldContain("holds no test host");
            lines[0].ShouldEndWith("See 'bobcat help resident'.");
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task a_path_that_does_not_exist_is_refused_in_one_line()
    {
        var result = await Tool.Run("resident", "/no/such/path", "--list");

        result.ExitCode.ShouldBe(1);
        var lines = Tool.Lines(result.Error);
        lines.Length.ShouldBe(1, result.Error);
        lines[0].ShouldBe("bobcat resident: There is nothing at '/no/such/path'. Name the spec project's test host: its executable, its build output folder, or its .csproj. See 'bobcat help resident'.");
    }
}
