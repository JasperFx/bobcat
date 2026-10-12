using System.Diagnostics;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#470's stretch, measured rather than assumed: the Microsoft.Testing.Platform filter that
/// runs exactly ONE TUnit test addressed as <c>Namespace.Class.Method</c>. TUnit filters by tree-node
/// path, <c>/assembly/namespace/class/method</c>, where the namespace is ONE segment with its dots
/// intact and a nested class is its CLR name (<c>Outer+Inner</c>). This is the evidence
/// SpecFilterArguments' TUnit translation is built on; the per-segment OR cases below are why it
/// refuses a selection that is not a cross product (the option is accepted once, and a whole-path
/// alternative is a parse error).
/// </summary>
public class TUnitTreeNodeFilterTests
{
    private const string Namespace = "Bobcat.TUnit.Tests.Filtering";

    [Theory]
    [InlineData("/*/" + Namespace + "/Alpha/one", new[] { "Alpha.one" })]
    [InlineData("/*/" + Namespace + "/Beta/one", new[] { "Beta.one" })]
    // The assembly segment can be named instead of wildcarded.
    [InlineData("/Bobcat.TUnit.Tests/" + Namespace + "/Alpha/one", new[] { "Alpha.one" })]
    // A nested class is its CLR name, '+' and all; "Outer.Inner" and "Inner" match nothing.
    [InlineData("/*/" + Namespace + "/Outer+Inner/one", new[] { "Outer.Inner.one" })]
    // A data-driven test: the method path selects every case of it.
    [InlineData("/*/" + Namespace + "/Gamma/with_arguments",
        new[] { "Gamma.with_arguments(1)", "Gamma.with_arguments(2)" })]
    // Segments match whole, not by prefix.
    [InlineData("/*/" + Namespace + "/Alpha/on", new string[0])]
    // And a method name alone is ambiguous across classes, which is why the class segment matters.
    [InlineData("/*/*/*/one", new[] { "Alpha.one", "Beta.one", "Outer.Inner.one" })]
    public async Task a_tree_node_path_runs_exactly_the_tests_it_names(string filter, string[] expected)
    {
        var host = WorkerHosts.Locate("Bobcat.TUnit.Tests");
        File.Exists(host).ShouldBeTrue($"The TUnit host was not built at {host}");

        var probe = Path.Combine(Path.GetTempPath(), $"bobcat-tunit-probe-{Guid.NewGuid():N}.txt");
        try
        {
            var info = new ProcessStartInfo(host)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            info.ArgumentList.Add("--treenode-filter");
            info.ArgumentList.Add(filter);
            info.ArgumentList.Add("--no-banner");
            info.Environment["BOBCAT_TUNIT_PROBE"] = probe;

            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            var ran = File.Exists(probe) ? File.ReadAllLines(probe).Order(StringComparer.Ordinal).ToArray() : [];
            ran.ShouldBe(expected, $"stdout:\n{await output}\nstderr:\n{await error}");

            // MTP exits 8 ("zero tests ran") for a filter that matched nothing, 0 otherwise.
            process.ExitCode.ShouldBe(expected.Length == 0 ? 8 : 0);
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
