using System.Reflection;
using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Alba.Tests;

public class ContentRootTests
{
    [Fact]
    public void with_content_root_is_fluent()
    {
        var resource = new AlbaResource<TestApp>();
        resource.WithContentRoot("/tmp/host").ShouldBeSameAs(resource);
    }

    [Fact]
    public void with_content_root_is_reported_as_the_resolution()
    {
        var resource = new AlbaResource<TestApp>().WithContentRoot("/tmp/host");
        resource.ContentRoot.Path.ShouldBe("/tmp/host");
        resource.ContentRoot.Source.ShouldBe("WithContentRoot");
    }

    [Fact]
    public void directory_not_found_is_wrapped_with_actionable_guidance()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new DirectoryNotFoundException("doubled path"), "MySample");

        var config = wrapped.ShouldBeOfType<BobcatConfigurationException>();
        config.Message.ShouldContain("WebApplicationFactoryContentRoot");
        config.Message.ShouldContain("WithContentRoot");
        config.Message.ShouldContain("MySample");
        config.InnerException.ShouldBeOfType<DirectoryNotFoundException>();
    }

    [Fact]
    public void no_solution_file_above_the_test_output_is_wrapped_too()
    {
        // WebApplicationFactory throws this from UseSolutionRelativeContentRoot before any
        // configure callback runs, so it cannot be pre-empted — only explained.
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new InvalidOperationException("Solution root could not be located using application root /x/bin."), "MySample");

        wrapped.ShouldBeOfType<BobcatConfigurationException>().Message.ShouldContain("WithContentRoot");
    }

    [Fact]
    public void the_resolved_root_is_named_in_the_guidance()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new DirectoryNotFoundException("x"), "MySample", "/repo/src/MySample (manifest)");

        wrapped.Message.ShouldContain("/repo/src/MySample (manifest)");
    }

    [Fact]
    public void unrelated_exceptions_pass_through_unchanged()
    {
        var original = new InvalidOperationException("boom");
        AlbaResourceDiagnostics.WrapStartException(original, "MySample").ShouldBeSameAs(original);
    }
}

/// <summary>
/// <see cref="AlbaContentRoot"/> against synthetic repository layouts (issue #62 gap 9). Every
/// test builds its own temp tree, so the order of precedence and each layout are pinned without
/// depending on where this test process happens to be running from.
/// </summary>
public class AlbaContentRootResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bobcat-content-root", Guid.NewGuid().ToString("N"));
    private static readonly AssemblyName entryPoint = new("My.Web, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

    public AlbaContentRootResolutionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void file(string path, string content = "") => File.WriteAllText(path, content);

    private void manifest(string baseDirectory, Dictionary<string, string> entries)
        => file(Path.Combine(baseDirectory, AlbaContentRoot.ManifestFileName), JsonSerializer.Serialize(entries));

    [Fact]
    public void src_layout_resolves_to_the_project_directory_through_the_manifest_in_the_test_output()
    {
        // <repo>/repo.sln, <repo>/src/My.Web/My.Web.csproj, tests at <repo>/src/My.Web.Tests/bin/Debug/net10.0.
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("src", "My.Web");
        file(Path.Combine(project, "My.Web.csproj"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");
        manifest(bin, new() { [entryPoint.FullName] = project });

        var resolution = AlbaContentRoot.Resolve(entryPoint, bin);

        resolution.Path.ShouldBe(project);
        resolution.Source.ShouldContain(AlbaContentRoot.ManifestFileName);
    }

    [Fact]
    public void the_manifest_is_read_from_the_test_output_directory_not_the_working_directory()
    {
        // The working directory of this test process is not <bin>; WebApplicationFactory's own
        // File.Exists("MvcTestingAppManifest.json") would miss this file and fall through to its
        // unchecked <solution>/<assembly> guess. Ours reads it from where the build put it.
        Environment.CurrentDirectory.ShouldNotBe(_root);
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("src", "My.Web");
        var bin = dir("out");
        manifest(bin, new() { [entryPoint.FullName] = project });

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(project);
    }

    [Fact]
    public void a_manifest_entry_matches_on_the_simple_assembly_name_when_the_version_differs()
    {
        var project = dir("src", "My.Web");
        var bin = dir("out");
        manifest(bin, new() { ["My.Web, Version=9.9.9.9, Culture=neutral, PublicKeyToken=null"] = project });

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(project);
    }

    [Fact]
    public void a_manifest_entry_pointing_at_a_missing_directory_is_skipped()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("src", "My.Web");
        file(Path.Combine(project, "My.Web.csproj"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");
        manifest(bin, new() { [entryPoint.FullName] = Path.Combine(_root, "gone") });

        // Falls through to the project-file search, which finds the real one.
        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(project);
    }

    [Fact]
    public void a_tilde_manifest_entry_means_the_test_output_directory()
    {
        var bin = dir("out");
        manifest(bin, new() { [entryPoint.FullName] = "~" });

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(bin);
    }

    [Fact]
    public void sibling_layout_resolves_solution_relative_without_a_manifest()
    {
        // <repo>/repo.sln, <repo>/My.Web — WebApplicationFactory's own happy path, kept.
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("My.Web");
        var bin = dir("My.Web.Tests", "bin", "Debug", "net10.0");

        var resolution = AlbaContentRoot.Resolve(entryPoint, bin);

        resolution.Path.ShouldBe(project);
        resolution.Source.ShouldContain("solution directory");
    }

    [Fact]
    public void src_layout_without_a_manifest_finds_the_project_file_below_the_solution()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("src", "My.Web");
        file(Path.Combine(project, "My.Web.csproj"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");

        var resolution = AlbaContentRoot.Resolve(entryPoint, bin);

        resolution.Path.ShouldBe(project);
        resolution.Source.ShouldContain("My.Web.csproj");
    }

    [Fact]
    public void nested_tests_layout_finds_the_host_project_without_the_attribute()
    {
        // <repo>/samples/My.Web/My.Web.csproj with the tests at <repo>/samples/My.Web/Tests — the
        // layout docs/sample-wiring.md footgun 2 is about.
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("samples", "My.Web");
        file(Path.Combine(project, "My.Web.csproj"));
        var bin = dir("samples", "My.Web", "Tests", "bin", "Debug", "net10.0");

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(project);
    }

    [Fact]
    public void a_slnx_file_marks_the_solution_directory_too()
    {
        file(Path.Combine(dir(), "repo.slnx"));
        var project = dir("src", "My.Web");
        file(Path.Combine(project, "My.Web.csproj"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(project);
    }

    [Fact]
    public void the_project_search_never_descends_into_bin_obj_or_dot_directories()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var decoy = dir("src", "Other", "bin", "My.Web");
        file(Path.Combine(decoy, "My.Web.csproj"));
        var hidden = dir(".git", "My.Web");
        file(Path.Combine(hidden, "My.Web.csproj"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");

        // Nothing legitimate to find: the fallback is the test output, not a decoy.
        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(bin);
    }

    [Fact]
    public void nothing_found_falls_back_to_the_test_output_directory_which_exists()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var bin = dir("src", "My.Web.Tests", "bin", "Debug", "net10.0");

        var resolution = AlbaContentRoot.Resolve(entryPoint, bin);

        resolution.Path.ShouldBe(bin);
        resolution.Source.ShouldContain("test output directory");
    }

    [Fact]
    public void no_solution_file_anywhere_still_falls_back_to_the_test_output_directory()
    {
        // Temp is not under a solution; neither is anything above it.
        var bin = dir("out");
        AlbaContentRoot.FindSolutionDirectory(bin).ShouldBeNull();

        AlbaContentRoot.Resolve(entryPoint, bin).Path.ShouldBe(bin);
    }

    [Fact]
    public void an_explicit_test_contentroot_setting_is_left_to_webapplicationfactory()
    {
        var variable = "ASPNETCORE_TEST_CONTENTROOT_MY_WEB";
        var bin = dir("out");
        manifest(bin, new() { [entryPoint.FullName] = dir("src", "My.Web") });

        Environment.SetEnvironmentVariable(variable, "/somewhere/else");
        try
        {
            var resolution = AlbaContentRoot.Resolve(entryPoint, bin);
            resolution.Path.ShouldBeNull();
            resolution.Source.ShouldContain(variable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void an_attribute_on_a_test_assembly_is_honoured_with_its_marker_file()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var project = dir("samples", "My.Web");
        file(Path.Combine(project, "appsettings.json"), "{}");
        var bin = dir("samples", "My.Web", "Tests", "bin", "Debug", "net10.0");

        // This test assembly carries the attribute below (see AssemblyAttributes in this file);
        // its relative path is the nested-Tests one, and the marker exists in the project directory.
        var resolution = AlbaContentRoot.Resolve(entryPoint, bin, [typeof(AlbaContentRootResolutionTests).Assembly]);

        resolution.Path.ShouldBe(Path.GetFullPath(project));
        resolution.Source.ShouldContain("WebApplicationFactoryContentRoot");
    }

    [Fact]
    public void the_manifest_beats_the_attribute()
    {
        file(Path.Combine(dir(), "repo.sln"));
        var attributed = dir("samples", "My.Web");
        file(Path.Combine(attributed, "appsettings.json"), "{}");
        var bin = dir("samples", "My.Web", "Tests", "bin", "Debug", "net10.0");
        var fromManifest = dir("elsewhere");
        manifest(bin, new() { [entryPoint.FullName] = fromManifest });

        AlbaContentRoot.Resolve(entryPoint, bin, [typeof(AlbaContentRootResolutionTests).Assembly])
            .Path.ShouldBe(fromManifest);
    }
}

/// <summary>
/// The real thing: <c>src/Bobcat.Alba.SampleWeb</c> sits under <c>src/</c>, exactly the layout
/// WebApplicationFactory's solution-relative guess gets wrong (it would look for
/// <c>&lt;repo&gt;/Bobcat.Alba.SampleWeb</c>). No attribute, no <c>WithContentRoot</c>.
/// </summary>
public class SampleWebContentRootTests
{
    [Fact]
    public void the_sample_web_project_under_src_resolves_to_its_own_directory()
    {
        var resolution = AlbaContentRoot.Resolve(typeof(SampleWeb.Program).Assembly);

        resolution.Path.ShouldNotBeNull();
        Path.GetFileName(Path.TrimEndingDirectorySeparator(resolution.Path!)).ShouldBe("Bobcat.Alba.SampleWeb");
        File.Exists(Path.Combine(resolution.Path!, "Bobcat.Alba.SampleWeb.csproj")).ShouldBeTrue();
    }

    [Fact]
    public async Task a_host_under_src_starts_with_its_project_directory_as_content_root()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>();
        await resource.Start();

        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/content-root"));
        var contentRoot = Path.TrimEndingDirectorySeparator(await result.ReadAsTextAsync());

        Path.GetFileName(contentRoot).ShouldBe("Bobcat.Alba.SampleWeb");
        resource.ContentRoot.Path.ShouldNotBeNull();
        Path.TrimEndingDirectorySeparator(resource.ContentRoot.Path!).ShouldBe(contentRoot);
    }

    [Fact]
    public async Task with_content_root_still_wins_over_resolution()
    {
        await using var resource = new AlbaResource<SampleWeb.Program>().WithContentRoot(AppContext.BaseDirectory);
        await resource.Start();

        var result = await resource.AlbaHost.Scenario(s => s.Get.Url("/content-root"));
        Path.TrimEndingDirectorySeparator(await result.ReadAsTextAsync())
            .ShouldBe(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }
}
