using System.Text.Json;
using Alba;
using Bobcat.Alba;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Alba.Tests;

/// <summary>
/// Issue #274: a host that failed to start reported nothing but a path, with the project name in
/// it twice.
/// </summary>
/// <remarks>
/// <para>
/// <c>Resource 'AlbaHost' failed to start: …/samples/ShipmentTracking/ShipmentTracking/</c> — and
/// that was the whole message. The doubling is not Bobcat's: it is WebApplicationFactory's
/// last-resort <c>&lt;solution dir&gt;/&lt;assembly name&gt;</c> fallback, taken <em>unchecked</em>,
/// landing in a repository whose solution file sits in a directory named after the project. An
/// ordinary layout, and the resulting path looks like a framework bug.
/// </para>
/// <para>
/// Two facts settle it, and the tests below pin both. Bobcat's own resolver gets that layout
/// right — <c>AlbaResource&lt;TProgram&gt;</c> would never have produced the doubled path. But the
/// reporter used the factory-delegate <see cref="AlbaResource"/>, which builds the host from the
/// caller's lambda, never sees a <c>TProgram</c>, and so cannot resolve anything — and it was not
/// even explaining the failure it passed up.
/// </para>
/// </remarks>
public class DoubledContentRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bobcat-doubled-root", Guid.NewGuid().ToString("N"));

    public DoubledContentRootTests() => Directory.CreateDirectory(_root);

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

    // --- Bobcat's own resolver handles the layout that trips WebApplicationFactory -------------

    [Fact]
    public void the_project_file_beside_the_solution_file_resolves_to_that_directory()
    {
        // The reported shape: samples/ShipmentTracking holds BOTH ShipmentTracking.sln and
        // ShipmentTracking.csproj, with the spec project nested under it. WebApplicationFactory's
        // fallback appends the assembly name to the solution directory and lands one level too
        // deep; the search checks the solution directory itself first, so it does not.
        var project = dir("samples", "ShipmentTracking");
        File.WriteAllText(Path.Combine(project, "ShipmentTracking.sln"), "");
        File.WriteAllText(Path.Combine(project, "ShipmentTracking.csproj"), "");
        var bin = dir("samples", "ShipmentTracking", "Specs", "bin", "Debug", "net10.0");

        var resolution = AlbaContentRoot.Resolve(new System.Reflection.AssemblyName("ShipmentTracking"), bin);

        resolution.Path.ShouldBe(project);
        // Never the doubled path WebApplicationFactory would have guessed.
        resolution.Path.ShouldNotBe(Path.Combine(project, "ShipmentTracking"));
    }

    [Fact]
    public void a_genuinely_nested_same_named_directory_is_still_preferred_when_it_exists()
    {
        // The counter-case, so the rule above is not mistaken for "never nest". <solution>/<name>
        // is checked first and is a real layout; it just has to exist.
        var solution = dir("repo");
        File.WriteAllText(Path.Combine(solution, "repo.sln"), "");
        var nested = dir("repo", "ShipmentTracking");
        var bin = dir("repo", "Specs", "bin", "Debug", "net10.0");

        var resolution = AlbaContentRoot.Resolve(new System.Reflection.AssemblyName("ShipmentTracking"), bin);

        resolution.Path.ShouldBe(nested);
    }

    // --- the failure now says what went wrong -------------------------------------------------

    [Fact]
    public void a_doubled_path_is_named_as_such()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new DirectoryNotFoundException("/repo/samples/ShipmentTracking/ShipmentTracking/"), "AlbaHost");

        var message = wrapped.ShouldBeOfType<BobcatConfigurationException>().Message;

        // The one line the issue asked for: the root it tried, and that it is not there.
        message.ShouldContain("/repo/samples/ShipmentTracking/ShipmentTracking/");
        message.ShouldContain("does not exist");
        // …plus why it looks the way it does, which is the part nobody could guess.
        message.ShouldContain("last two segments repeat");
        message.ShouldContain("ShipmentTracking/ShipmentTracking");
    }

    [Fact]
    public void an_undoubled_missing_path_is_still_named_without_inventing_a_cause()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new DirectoryNotFoundException("/repo/nowhere"), "AlbaHost");

        var message = wrapped.ShouldBeOfType<BobcatConfigurationException>().Message;

        message.ShouldContain("/repo/nowhere");
        message.ShouldContain("does not exist");
        message.ShouldNotContain("last two segments repeat");
    }

    [Fact]
    public void the_solution_root_failure_has_its_path_read_out_too()
    {
        var wrapped = AlbaResourceDiagnostics.WrapStartException(
            new InvalidOperationException(
                "Solution root could not be located using application root /repo/Specs/bin/Debug/net10.0."),
            "AlbaHost");

        wrapped.ShouldBeOfType<BobcatConfigurationException>()
            .Message.ShouldContain("/repo/Specs/bin/Debug/net10.0");
    }

    [Fact]
    public void a_message_that_names_no_path_adds_nothing_rather_than_guessing()
    {
        AlbaResourceDiagnostics.DescribeSuspectRoot(new DirectoryNotFoundException("could not find it"))
            .ShouldBe("");
    }

    // --- the advice matches the form you are actually using ------------------------------------

    [Fact]
    public void the_delegate_form_is_told_where_it_can_actually_set_the_root()
    {
        // No TProgram means no Bobcat resolution, so "call AlbaResource<TProgram>.WithContentRoot"
        // is advice for a type this caller is not using. The lambda is where the root gets pinned.
        var help = AlbaResourceDiagnostics.ContentRootHelp("AlbaHost");

        help.ShouldContain("UseContentRoot");
        help.ShouldContain("factory you passed to AlbaResource");
    }

    [Fact]
    public void the_typed_form_keeps_its_own_advice()
    {
        var help = AlbaResourceDiagnostics.ContentRootHelp("MySample", "/repo/src/MySample (manifest)");

        help.ShouldContain("AlbaResource<TProgram>.WithContentRoot");
        help.ShouldContain("/repo/src/MySample (manifest)");
    }

    // --- and the delegate form explains instead of passing a bare path up ----------------------

    [Fact]
    public async Task the_factory_delegate_resource_wraps_a_content_root_failure()
    {
        var resource = new AlbaResource(
            () => Task.FromException<IAlbaHost>(
                new DirectoryNotFoundException("/repo/samples/ShipmentTracking/ShipmentTracking/")));

        var ex = await Should.ThrowAsync<BobcatConfigurationException>(() => resource.Start());

        ex.Message.ShouldContain("last two segments repeat");
        ex.InnerException.ShouldBeOfType<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task an_unrelated_start_failure_is_not_dressed_up_as_a_content_root_problem()
    {
        var resource = new AlbaResource(
            () => Task.FromException<IAlbaHost>(new InvalidOperationException("the broker is down")));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => resource.Start());

        ex.Message.ShouldBe("the broker is down");
        ex.ShouldNotBeOfType<BobcatConfigurationException>();
    }
}
