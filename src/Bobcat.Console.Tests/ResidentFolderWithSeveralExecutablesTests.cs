using Bobcat.Console.Specs;
using JasperFx.CommandLine;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#797: a spec project that references the application it tests has two executables in its
/// build output folder, and <c>bobcat resident &lt;folder&gt;</c> refused it. With several, the
/// folder's own project's assembly wins; failing that, the only one carrying a test framework; only
/// a folder still ambiguous after both is refused.
/// </summary>
public class ResidentFolderWithSeveralExecutablesTests : IDisposable
{
    private const string Specs = "Bobcat.Console.SampleWeb.Specs";
    private const string App = "Bobcat.Console.SampleWeb";

    private readonly string _directory = Directory.CreateTempSubdirectory("bobcat-797-").FullName;

    /// <summary><c>Bobcat.Console.SampleWeb.Specs</c>, which references the executable <c>Bobcat.Console.SampleWeb</c>.</summary>
    private static string specsFolder => Tool.BinDirectoryOf(Specs);

    private static void listsTheSpecs((int ExitCode, string Output, string Error) result)
    {
        result.ExitCode.ShouldBe(0, result.Output + result.Error);
        result.Output.ShouldContain($"{Specs} (projected/xunit)");
        result.Output.ShouldContain("Greeting/greets by name");
    }

    [Fact]
    public void the_fixture_really_holds_two_executables()
    {
        // Guards the cases below against passing vacuously: were the web app's apphost not copied
        // beside the specs, there would be nothing to choose between.
        File.Exists(Path.Combine(specsFolder, App + ".runtimeconfig.json")).ShouldBeTrue();
        File.Exists(Path.Combine(specsFolder, executable(App))).ShouldBeTrue();
        File.Exists(Path.Combine(specsFolder, executable(Specs))).ShouldBeTrue();
    }

    [Fact]
    public async Task a_spec_projects_build_output_that_also_holds_the_web_app_it_tests()
        => listsTheSpecs(await Tool.Run("resident", specsFolder, "--list"));

    [Fact]
    public async Task with_no_project_above_the_folder_the_one_test_host_is_chosen()
    {
        // The same build output, copied where no .csproj owns it: only the deps files can tell the
        // test host from the application under test.
        var copy = Path.Combine(_directory, "copied");
        copyDirectory(specsFolder, copy);

        listsTheSpecs(await Tool.Run("resident", copy, "--list"));
    }

    [Fact]
    public async Task the_owning_projects_assembly_wins_over_another_test_host()
    {
        // BankAccountES.Tests built by Tests.csproj, beside a second test host it references and the
        // application. Two of the three carry xUnit, so only the project can decide, and it decides
        // by its AssemblyName rather than its file name.
        var project = Path.Combine(_directory, "Tests");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>BankAccountES.Tests</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var bin = Path.Combine(project, "bin", "Debug", "net10.0");
        fakeExecutable(bin, "BankAccountES", "Microsoft.AspNetCore.OpenApi");
        fakeExecutable(bin, "BankAccountES.Tests", "xunit.v3.core.mtp-v2", "Microsoft.Testing.Platform");
        fakeExecutable(bin, "Shared.Tests", "xunit.v3.core.mtp-v2", "Microsoft.Testing.Platform");

        var host = await TestHostPath.ResolveAsync(bin, null, "Debug");

        host.ShouldBe(Path.Combine(bin, executable("BankAccountES.Tests")));
    }

    [Fact]
    public async Task several_executables_none_a_test_host_is_still_refused_naming_them()
    {
        var bin = Path.Combine(_directory, "neither");
        fakeExecutable(bin, "Billing", "Microsoft.AspNetCore.OpenApi");
        fakeExecutable(bin, "Shipping");

        refusedNaming(await Tool.Run("resident", bin, "--list"), bin, "Billing, Shipping");
    }

    [Fact]
    public async Task several_test_hosts_with_no_project_to_choose_is_still_refused_naming_them()
    {
        var bin = Path.Combine(_directory, "both");
        fakeExecutable(bin, "Orders.Tests", "xunit.v3.core.mtp-v2");
        fakeExecutable(bin, "Payments.Tests", "TUnit.Engine");

        refusedNaming(await Tool.Run("resident", bin, "--list"), bin, "Orders.Tests, Payments.Tests");
    }

    [Fact]
    public async Task a_refusal_is_a_usage_error_from_the_resolver()
    {
        var bin = Path.Combine(_directory, "direct");
        fakeExecutable(bin, "Billing");
        fakeExecutable(bin, "Shipping");

        var refusal = await Should.ThrowAsync<InvalidUsageException>(() => TestHostPath.ResolveAsync(bin, null, "Debug"));
        refusal.Message.ShouldBe($"'{bin}' holds 2 executables (Billing, Shipping); name the test host itself.");
    }

    private static void refusedNaming((int ExitCode, string Output, string Error) result, string bin, string candidates)
    {
        result.ExitCode.ShouldBe(1, result.Output + result.Error);
        var lines = Tool.Lines(result.Error);
        lines.Length.ShouldBe(1, result.Error);
        lines[0].ShouldBe($"bobcat resident: '{bin}' holds 2 executables ({candidates}); name the test host itself. See 'bobcat help resident'.");
    }

    private static string executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>
    /// What the resolver reads of an executable: the apphost, the runtimeconfig beside it, and a deps
    /// file listing the executable itself and <paramref name="libraries"/>.
    /// </summary>
    private static void fakeExecutable(string directory, string name, params string[] libraries)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, executable(name)), "");
        File.WriteAllText(Path.Combine(directory, name + ".runtimeconfig.json"), "{}");

        var entries = new[] { name }.Concat(libraries)
            .Select(x => $"\"{x}/1.0.0\": {{ \"type\": \"package\" }}");
        File.WriteAllText(Path.Combine(directory, name + ".deps.json"), $"{{ \"libraries\": {{ {string.Join(", ", entries)} }} }}");
    }

    private static void copyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.Replace(from, to), File.GetUnixFileMode(file));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
