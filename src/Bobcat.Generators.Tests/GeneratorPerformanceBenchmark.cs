using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Shouldly;
using Xunit;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#815: where the generator spends its time on a large spec project, measured the way a consumer
/// pays for it — a cold run, then an incremental run after a one-line edit to one file, which re-runs
/// every <c>CreateSyntaxProvider</c> transform over every tree because the semantic model is new.
/// </summary>
/// <remarks>
/// <para>
/// The synthetic project has the reporter's shape: many spec classes over a few shared helpers that wrap
/// the vocabulary's acts one and two deep, each spec calling several helpers, plus filler files of plain
/// code. Sizes come from <c>BOBCAT_BENCH_SPEC_FILES</c> / <c>BOBCAT_BENCH_FILLER_FILES</c>, defaulting
/// to a size the test suite can afford; the reporter's project is about 260 spec files (1,260 specs) in
/// about 2,000 files, which is <c>BOBCAT_BENCH_SPEC_FILES=260 BOBCAT_BENCH_FILLER_FILES=1700</c>.
/// </para>
/// <para>
/// <see cref="the_generator_reads_a_shared_helper_once_per_compilation"/> is the regression test;
/// <see cref="report_where_the_time_goes"/> prints the per-step figures and asserts nothing about them.
/// </para>
/// </remarks>
public class GeneratorPerformanceBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void report_where_the_time_goes()
    {
        var specFiles = sizeFrom("BOBCAT_BENCH_SPEC_FILES", 20);
        var fillerFiles = sizeFrom("BOBCAT_BENCH_FILLER_FILES", 40);

        var project = SyntheticSpecProject.Build(specFiles, fillerFiles);
        var (cold, incremental) = run(project);

        var report = new List<string>();
        void line(string text) { output.WriteLine(text); report.Add(text); }

        line($"{specFiles} spec files ({specFiles * SyntheticSpecProject.SpecsPerFile} specs), {fillerFiles} filler files");
        line($"cold run:        {cold.Elapsed.TotalMilliseconds,8:F0} ms");
        line($"incremental run: {incremental.Elapsed.TotalMilliseconds,8:F0} ms");
        line("");
        line("incremental run, by tracked step (transform time, every tree):");
        foreach (var (name, elapsed, count) in stepTimes(incremental.Result).OrderByDescending(x => x.Elapsed))
            line($"  {name,-16} {elapsed.TotalMilliseconds,8:F0} ms over {count} nodes");

        // BOBCAT_BENCH_REPORT names a file to append the figures to, so a run from the command line keeps them
        if (Environment.GetEnvironmentVariable("BOBCAT_BENCH_REPORT") is { Length: > 0 } path)
            File.AppendAllLines(path, report.Append(""));

        incremental.Result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    /// <summary>
    /// A helper every spec calls is bound and walked once per compilation, not once per caller: the cost of
    /// reading the specs grows with the number of specs, not with specs × helpers × wrapper depth. Pinned by
    /// counting the helper walks the reader makes rather than by timing, which no CI box agrees about.
    /// </summary>
    [Fact]
    public void the_generator_reads_a_shared_helper_once_per_compilation()
    {
        var project = SyntheticSpecProject.Build(specFiles: 8, fillerFiles: 0);

        var (cold, incremental) = run(project);
        var walks = ProjectedRoles.HelperWalks(cold.Compilation) + ProjectedRoles.HelperWalks(incremental.Compilation);

        // Two compilations (cold and incremental), each reading every helper once. The vocabulary's own methods
        // are metadata-free source here too, so they count as helpers that make no act.
        var helpersInSource = SyntheticSpecProject.HelperMethodCount + SyntheticSpecProject.VocabularyMethodCount;
        output.WriteLine($"helper walks: {walks}, helpers in source: {helpersInSource}, specs: {8 * SyntheticSpecProject.SpecsPerFile}");
        walks.ShouldBeLessThanOrEqualTo(helpersInSource * 2);
    }

    /// <summary>
    /// A call is bound only when its name is one a step, an act or an assertion dialect could answer to: the
    /// cost of reading a project grows with its steps, not with every call in every body. Pinned by counting the
    /// call sites the four reading passes bind, against the steps and acts the synthetic project actually makes.
    /// </summary>
    [Fact]
    public void the_generator_binds_only_the_calls_a_step_could_answer_to()
    {
        var project = SyntheticSpecProject.Build(specFiles: 8, fillerFiles: 8);

        var (cold, _) = run(project);
        var bound = StepCallCandidates.CallsBound(cold.Compilation);

        var specs = 8 * SyntheticSpecProject.SpecsPerFile;
        var invocations = cold.Compilation.SyntaxTrees
            .Sum(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Count());

        // A spec body is read by four passes, each binding at most its three acts and its two arranging calls;
        // every arranging helper binds its act, its opening helper and that helper's act.
        var perSpec = 4 * 5;
        var perHelperFile = SyntheticSpecProject.ArrangersPerFile * 3;
        var ceiling = specs * perSpec + SyntheticSpecProject.HelperFiles * perHelperFile;
        output.WriteLine($"calls bound: {bound}, ceiling: {ceiling}, invocations in source: {invocations}");
        bound.ShouldBeLessThanOrEqualTo(ceiling);
        bound.ShouldBeLessThan(invocations);
    }

    private static int sizeFrom(string variable, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), out var size) && size > 0 ? size : fallback;

    private static (Timed Cold, Timed Incremental) run(SyntheticSpecProject project)
    {
        var compilation = project.Compilation;
        var driver = (GeneratorDriver)CSharpGeneratorDriver.Create(
            [new BobcatGenerator().AsSourceGenerator()],
            parseOptions: SyntheticSpecProject.ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        var cold = Stopwatch.StartNew();
        driver = driver.RunGenerators(compilation);
        cold.Stop();
        var coldResult = driver.GetRunResult();

        // The reporter's edit: one private member in one spec file
        var edited = project.EditOneSpecFile();

        var warm = Stopwatch.StartNew();
        driver = driver.RunGenerators(edited);
        warm.Stop();
        var warmResult = driver.GetRunResult();

        return (new Timed(cold.Elapsed, coldResult, compilation), new Timed(warm.Elapsed, warmResult, edited));
    }

    private sealed record Timed(TimeSpan Elapsed, GeneratorDriverRunResult Result, Compilation Compilation);

    private static IEnumerable<(string Name, TimeSpan Elapsed, int Count)> stepTimes(GeneratorDriverRunResult result)
    {
        foreach (var (name, steps) in result.Results.Single().TrackedSteps)
        {
            var elapsed = TimeSpan.Zero;
            foreach (var step in steps) elapsed += step.ElapsedTime;
            yield return (name, elapsed, steps.Length);
        }
    }
}

/// <summary>A spec project of the shape bobcat#815 describes, as in-memory syntax trees.</summary>
internal sealed class SyntheticSpecProject
{
    public const int SpecsPerFile = 5;
    public const int HelperFiles = 4;
    public const int LeavesPerFile = 12;
    public const int MidsPerFile = 8;
    public const int ArrangersPerFile = 10;
    public const int FanOut = 4;
    public const int HelperMethodCount = HelperFiles * (LeavesPerFile + MidsPerFile + ArrangersPerFile * 2);
    public const int VocabularyMethodCount = 8;

    public static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithFeatures([new KeyValuePair<string, string>("InterceptorsNamespaces", "Bobcat.Generated")]);

    public CSharpCompilation Compilation { get; }
    private readonly SyntaxTree _firstSpecFile;

    private SyntheticSpecProject(CSharpCompilation compilation, SyntaxTree firstSpecFile)
    {
        Compilation = compilation;
        _firstSpecFile = firstSpecFile;
    }

    public static SyntheticSpecProject Build(int specFiles, int fillerFiles)
    {
        var trees = new List<SyntaxTree> { parse(Usings + Domain + Vocabulary, "Vocabulary.cs") };

        for (var h = 0; h < HelperFiles; h++) trees.Add(parse(Usings + helpers(h), $"Helpers{h}.cs"));
        for (var s = 0; s < specFiles; s++) trees.Add(parse(Usings + specs(s), $"Specs{s}.cs"));
        for (var f = 0; f < fillerFiles; f++) trees.Add(parse(Usings + filler(f), $"Filler{f}.cs"));

        var compilation = CSharpCompilation.Create(
            "LargeSpecProject",
            trees,
            GeneratorHarness.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return new SyntheticSpecProject(compilation, trees[1 + HelperFiles]);
    }

    /// <summary>The same compilation with one private member of one spec file renamed.</summary>
    public CSharpCompilation EditOneSpecFile()
    {
        var text = _firstSpecFile.GetText().ToString().Replace("private static int counter;", "private static int counterEdited;");
        return Compilation.ReplaceSyntaxTree(_firstSpecFile, parse(text, _firstSpecFile.FilePath));
    }

    private static SyntaxTree parse(string source, string path)
        => CSharpSyntaxTree.ParseText(SourceText.From(source), ParseOptions, path);

    private const string Usings =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading.Tasks;
        using Bobcat;
        using Bank;
        using Vocab;
        using Helpers;
        using Shouldly;
        using Xunit;

        """;

    private const string Domain =
        """
        namespace Bank
        {
            public record OpenAccount(Guid Id, string Owner);
            public record Deposit(Guid Id, decimal Amount);
            public record AccountOpened(Guid Id, string Owner);
            public record FundsDeposited(Guid Id, decimal Amount);
            public class Account { public Guid Id { get; set; } public decimal Balance { get; set; } }
            public class AccountSummary { public Guid Id { get; set; } }
        }

        """;

    private const string Vocabulary =
        """
        namespace Vocab
        {
            public abstract class ScenarioBase
            {
                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenEvents<TAggregate>(Guid id, params object[] events) where TAggregate : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(message))]
                public Task WhenReceived(object message) => Task.CompletedTask;

                [EventModelRole(SliceRole.Command, nameof(command))]
                [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
                public Task WhenPosted(object command, string route) => Task.CompletedTask;

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "GET")]
                public Task WhenReading<T>(string route) where T : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Event, nameof(events))]
                public void ThenEvents(params object[] events) { }

                [EventModelRole(SliceRole.ReadModel, nameof(T))]
                public Task<T> ThenReadModel<T>(object id) where T : class => Task.FromResult<T>(null!);

                public void ThenRefusedWith(string reason) { }

                public Task Log(string message) => Task.CompletedTask;
            }
        }

        """;

    /// <summary>
    /// The helper graph fans out, which is the shape that costs: an arranging helper calls several mid-level
    /// helpers, each of which calls several leaf helpers, so one spec reaches many source bodies through many
    /// paths. The leaves make no act, so every one of them is walked to the depth cap for nothing.
    /// </summary>
    private static string helpers(int file)
    {
        var lines = new List<string>
        {
            "namespace Helpers",
            "{",
            $"    public abstract class HelperBase{file} : {(file == 0 ? "ScenarioBase" : $"HelperBase{file - 1}")}",
            "    {",
            $"        private readonly List<string> _seen{file} = new();",
        };

        for (var i = 0; i < LeavesPerFile; i++)
        {
            lines.Add($"        protected string Leaf{file}_{i}(Guid id)");
            lines.Add("        {");
            lines.Add($"            _seen{file}.Add(id.ToString(\"N\"));");
            lines.Add($"            var joined = string.Join(\",\", _seen{file}.Where(x => x.Length > {i}).Select(x => x.Trim()));");
            lines.Add($"            return joined.ToUpperInvariant() + Math.Max(joined.Length, {i});");
            lines.Add("        }");
        }

        for (var i = 0; i < MidsPerFile; i++)
        {
            lines.Add($"        protected async Task Mid{file}_{i}(Guid id)");
            lines.Add("        {");
            lines.Add($"            await Log(\"mid {i}\");");
            for (var leaf = 0; leaf < FanOut; leaf++)
                lines.Add($"            Leaf{file}_{(i + leaf) % LeavesPerFile}(id);");
            lines.Add("        }");
        }

        for (var i = 0; i < ArrangersPerFile; i++)
        {
            // One deep: the helper calls the vocabulary's act directly
            lines.Add($"        protected Task Open{file}_{i}(Guid id) => WhenPosted(new OpenAccount(id, \"owner{i}\"), \"/accounts\");");
            // Two deep: a helper over a helper, plus arrangement and the fan-out before it
            lines.Add($"        protected async Task Arrange{file}_{i}(Guid id)");
            lines.Add("        {");
            for (var mid = 0; mid < FanOut; mid++)
                lines.Add($"            await Mid{file}_{(i + mid) % MidsPerFile}(id);");
            lines.Add($"            await GivenEvents<Account>(id, new AccountOpened(id, \"owner{i}\"));");
            lines.Add($"            await Open{file}_{i}(id);");
            lines.Add("        }");
        }

        lines.Add("    }");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    private static string specs(int file)
    {
        var lines = new List<string>
        {
            "namespace Specs",
            "{",
            $"    [BobcatFeature(\"Accounts {file}\")]",
            $"    public class AccountSpecs{file} : HelperBase{HelperFiles - 1}",
            "    {",
            "        private static int counter;",
        };

        for (var i = 0; i < SpecsPerFile; i++)
        {
            var helperFile = (file + i) % HelperFiles;
            var helper = (file * 7 + i) % ArrangersPerFile;
            var second = (file * 3 + i + 1) % ArrangersPerFile;
            lines.Add("        [Fact]");
            lines.Add($"        public async Task scenario_{i}()");
            lines.Add("        {");
            lines.Add("            // Given an account");
            lines.Add("            var id = Guid.NewGuid();");
            lines.Add($"            await Arrange{helperFile}_{helper}(id);");
            lines.Add($"            await Arrange{(helperFile + 1) % HelperFiles}_{second}(id);");
            lines.Add("            counter++;");
            lines.Add("            // When money is deposited");
            lines.Add("            await WhenReceived(new Deposit(id, 10m));");
            lines.Add("            // Then the deposit is recorded");
            lines.Add("            ThenEvents(new FundsDeposited(id, 10m));");
            lines.Add("            var account = await ThenReadModel<Account>(id);");
            lines.Add("            account.Balance.ShouldBe(10m);");
            lines.Add("            string.Join(\",\", new[] { \"a\", \"b\" }).Length.ShouldBeGreaterThan(0);");
            lines.Add("        }");
        }

        lines.Add("    }");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    private static string filler(int file)
    {
        var lines = new List<string>
        {
            "namespace Filler",
            "{",
            $"    public class Service{file}",
            "    {",
            "        private readonly List<string> _items = new();",
        };

        for (var i = 0; i < 8; i++)
        {
            lines.Add($"        public string Method{i}(int value)");
            lines.Add("        {");
            lines.Add($"            _items.Add(value.ToString());");
            lines.Add($"            var joined = string.Join(\"-\", _items.Where(x => x.Length > {i}));");
            lines.Add($"            return joined.Trim().ToUpperInvariant() + Math.Max(value, {i});");
            lines.Add("        }");
        }

        lines.Add("    }");
        lines.Add("}");
        return string.Join("\n", lines);
    }
}
