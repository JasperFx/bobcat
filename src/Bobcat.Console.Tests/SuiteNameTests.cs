using Bobcat.Console.Specs;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#795: <c>bobcat watch</c> reports a build under the name runs publish as their suite — the
/// assembly name — not the project file's name. A <c>Tests.csproj</c> building
/// <c>BankAccountES.Tests</c> reported "Tests", so the board could not put a broken build on its row.
/// </summary>
public class SuiteNameTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bobcat-suite-name-").FullName;

    private string projectAt(string fileName, string? assemblyName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                {(assemblyName is null ? "" : $"<AssemblyName>{assemblyName}</AssemblyName>")}
              </PropertyGroup>
            </Project>
            """);
        return path;
    }

    [Fact]
    public async Task the_assembly_name_when_it_differs_from_the_project_file()
    {
        var project = await SpecProject.ResolveAsync(projectAt("Tests.csproj", "BankAccountES.Tests"), null, "Debug");

        project.Name.ShouldBe("Tests");
        (await project.SuiteNameAsync()).ShouldBe("BankAccountES.Tests");
    }

    [Fact]
    public async Task the_project_file_name_when_no_assembly_name_is_set()
    {
        var project = await SpecProject.ResolveAsync(projectAt("Orders.Specs.csproj", null), null, "Debug");

        (await project.SuiteNameAsync()).ShouldBe("Orders.Specs");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
