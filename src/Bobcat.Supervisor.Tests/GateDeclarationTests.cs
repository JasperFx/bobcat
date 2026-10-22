using Bobcat.Resilience;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// The declaration half of the gate (bobcat#729): which frameworks a project runs on, how units
/// are named and tagged, where the host is expected, what the duration record predicts, and how
/// the results fold to one exit code. No test host.
/// </summary>
public class GateDeclarationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bobcat-gate-" + Guid.NewGuid().ToString("N"));

    public GateDeclarationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string project(string name, string frameworks)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".csproj");
        var element = frameworks.Contains(';') ? "TargetFrameworks" : "TargetFramework";
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><{element}>{frameworks}</{element}></PropertyGroup></Project>");
        return path;
    }

    // ─── frameworks ───────────────────────────────────────────────────

    [Fact]
    public void newest_is_by_version_not_ordinal()
    {
        TargetFrameworks.Newest(["net9.0", "net10.0", "net8.0"]).ShouldBe("net10.0");
        TargetFrameworks.VersionOf("net10.0-windows").ShouldBe(new Version(10, 0));
        TargetFrameworks.VersionOf("net48").ShouldBe(new Version(4, 8));
        TargetFrameworks.VersionOf("netstandard2.0").ShouldBeNull();
    }

    [Fact]
    public void the_selection_is_newest_by_default_all_on_request_or_a_pinned_list_with_fallback()
    {
        string[] declared = ["net9.0", "net10.0"];

        FrameworkSelection.Newest.Select(declared).ShouldBe(["net10.0"]);
        FrameworkSelection.All.Select(declared).ShouldBe(["net10.0", "net9.0"]);
        FrameworkSelection.Only("net9.0").Select(declared).ShouldBe(["net9.0"]);

        // The Alba shape: a net10.0-only project asked for net9.0 runs on what it has, once.
        FrameworkSelection.Only("net9.0").Select(["net10.0"]).ShouldBe(["net10.0"]);
        FrameworkSelection.Only("net9.0", "net10.0").Select(["net10.0"]).ShouldBe(["net10.0"]);
    }

    [Fact]
    public void the_command_line_spelling_parses()
    {
        FrameworkSelection.Parse(null).ShouldBe(FrameworkSelection.Newest);
        FrameworkSelection.Parse("all").ShouldBe(FrameworkSelection.All);
        FrameworkSelection.Parse("net9.0;net10.0").Frameworks.ShouldBe(["net9.0", "net10.0"]);
        FrameworkSelection.Parse("net9.0").ToString().ShouldBe("net9.0");
    }

    [Fact]
    public void declared_frameworks_are_read_from_the_project_file()
    {
        TargetFrameworks.Declared(project("Multi", "net9.0;net10.0")).ShouldBe(["net9.0", "net10.0"]);
        TargetFrameworks.Declared(project("Single", "net10.0")).ShouldBe(["net10.0"]);
        TargetFrameworks.Declared(Path.Combine(_root, "missing.csproj")).ShouldBeEmpty();
    }

    // ─── units ────────────────────────────────────────────────────────

    [Fact]
    public void one_unit_per_selected_framework_named_project_dot_framework()
    {
        var gate = new GateDeclaration { Frameworks = FrameworkSelection.All, Configuration = "Release" };
        gate.Tracks.Add(new Track("postgres"));
        gate.Project(project("CoreTests", "net9.0;net10.0"), workers: 2).On("postgres");
        gate.Project(project("Plain", "net10.0"));

        var units = gate.Units();

        units.Select(u => u.Name).ShouldBe(["CoreTests.net10.0", "CoreTests.net9.0", "Plain.net10.0"]);
        units[0].Workers.ShouldBe(2);
        units[0].Tracks.Select(t => t.Name).ShouldBe(["postgres"]);
        units[2].Tracks.ShouldBeEmpty();

        units[0].Executable.ShouldBe(Path.Combine(_root, "CoreTests", "bin", "Release", "net10.0",
            OperatingSystem.IsWindows() ? "CoreTests.exe" : "CoreTests"));
    }

    [Fact]
    public void newest_only_by_default()
    {
        var gate = new GateDeclaration();
        gate.Project(project("CoreTests", "net9.0;net10.0"));

        gate.Units().Select(u => u.Name).ShouldBe(["CoreTests.net10.0"]);
    }

    [Fact]
    public void a_project_may_name_its_frameworks_instead_of_being_read()
    {
        var gate = new GateDeclaration();
        gate.Project(Path.Combine(_root, "NotOnDisk", "NotOnDisk.csproj")).Targeting("net10.0");

        gate.Units().Single().Name.ShouldBe("NotOnDisk.net10.0");
    }

    [Fact]
    public void a_project_directory_resolves_to_the_project_file_in_it()
    {
        var path = project("ByDirectory", "net10.0");
        var gate = new GateDeclaration();

        gate.Project(Path.GetDirectoryName(path)!).Path.ShouldBe(path);
    }

    [Fact]
    public void the_unit_tag_is_prefix_slash_unit_and_null_without_a_prefix()
    {
        var gate = new GateDeclaration();
        gate.Project(project("CoreTests", "net10.0"));
        var unit = gate.Units().Single();

        unit.RunTagUnder("local-gates/wolverine-gate").ShouldBe("local-gates/wolverine-gate/CoreTests.net10.0");
        unit.RunTagUnder(null).ShouldBeNull();
    }

    [Fact]
    public void naming_a_track_the_gate_does_not_declare_is_an_error_not_a_unit_that_waits_forever()
    {
        var gate = new GateDeclaration();
        gate.Tracks.Add(new Track("postgres"));
        gate.Project(project("CoreTests", "net10.0")).On("rabbit");

        Should.Throw<InvalidOperationException>(() => gate.Units()).Message
            .ShouldContain("names track 'rabbit'");
    }

    [Fact]
    public void declaring_a_project_twice_is_an_error()
    {
        var gate = new GateDeclaration();
        var path = project("CoreTests", "net10.0");
        gate.Project(path);

        Should.Throw<ArgumentException>(() => gate.Project(path));
    }

    [Fact]
    public void a_project_whose_frameworks_cannot_be_read_says_so()
    {
        var gate = new GateDeclaration();
        gate.Project(Path.Combine(_root, "Ghost", "Ghost.csproj"));

        Should.Throw<InvalidOperationException>(() => gate.Units()).Message.ShouldContain("Targeting");
    }

    // ─── durations ────────────────────────────────────────────────────

    [Fact]
    public void durations_predict_the_mean_of_the_kept_runs_and_round_trip()
    {
        var path = Path.Combine(_root, ".bobcat", "gate-durations.json");

        var durations = GateDurations.Empty()
            .Record("A.net10.0", TimeSpan.FromSeconds(10))
            .Record("A.net10.0", TimeSpan.FromSeconds(20));
        durations.Predict("A.net10.0").ShouldBe(TimeSpan.FromSeconds(15));
        durations.Predict("B.net10.0").ShouldBeNull();

        durations.Save(path);
        GateDurations.Load(path).Predict("A.net10.0").ShouldBe(TimeSpan.FromSeconds(15));

        GateDurations.Load(Path.Combine(_root, "nowhere.json")).Units.ShouldBeEmpty();
    }

    [Fact]
    public void durations_keep_the_newest_five()
    {
        var durations = GateDurations.Empty();
        for (var i = 1; i <= 7; i++) durations.Record("A", TimeSpan.FromSeconds(i));

        // 3..7 kept: mean 5.
        durations.Predict("A").ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void an_unreadable_record_is_an_empty_one_never_a_failed_gate()
    {
        var path = Path.Combine(_root, "garbage.json");
        File.WriteAllText(path, "{ not json");

        GateDurations.Load(path).Units.ShouldBeEmpty();
    }

    // ─── results ──────────────────────────────────────────────────────

    private static GateUnit unit(string name)
        => new(new GateProject(Path.Combine(Path.GetTempPath(), name, name + ".csproj"), 1), "net10.0", [], 1, name);

    private static SupervisorResults results(params (string Uid, WorkerTestState State, int Attempts)[] tests)
        => new()
        {
            Tests = tests.Select(t => new TestReport
            {
                Uid = t.Uid,
                DisplayName = t.Uid,
                Attempts = Enumerable.Range(1, t.Attempts).Select(attempt => new SupervisorAttempt(
                    attempt,
                    new WorkerOutcome(t.Uid, t.Uid, attempt == t.Attempts ? t.State : WorkerTestState.Failed),
                    attempt == 1 ? AttemptPlacement.Batched : AttemptPlacement.IsolatedProcess,
                    attempt == t.Attempts ? Disposition.Pass : Disposition.RetryInFreshProcess("retry"))).ToList()
            }).ToList()
        };

    [Fact]
    public void the_gate_exit_code_is_the_worst_unit_and_verdicts_are_honest()
    {
        var clean = new GateUnitResult(unit("Clean"), results(("t", WorkerTestState.Passed, 1)), TimeSpan.FromSeconds(1), null, 1, null);
        var flaky = new GateUnitResult(unit("Flaky"), results(("t", WorkerTestState.Passed, 2)), TimeSpan.FromSeconds(1), null, 1, null);
        var failed = new GateUnitResult(unit("Failed"), results(("t", WorkerTestState.Failed, 1)), TimeSpan.FromSeconds(1), null, 1, null);
        var crashed = new GateUnitResult(unit("Crashed"), results(("t", WorkerTestState.Indeterminate, 1)), TimeSpan.FromSeconds(1), null, 1, null);
        var threw = new GateUnitResult(unit("Threw"), null, TimeSpan.FromSeconds(1), null, 1, null, new InvalidOperationException("no host"));
        var notRun = new GateUnitResult(unit("NotRun"), null, TimeSpan.Zero, null, 0, null);

        clean.Verdict.ShouldBe(GateVerdict.Passed);
        flaky.Verdict.ShouldBe(GateVerdict.PassedOnRetry);
        failed.Verdict.ShouldBe(GateVerdict.Failed);
        crashed.Verdict.ShouldBe(GateVerdict.Indeterminate);
        threw.Verdict.ShouldBe(GateVerdict.Indeterminate);
        notRun.Verdict.ShouldBe(GateVerdict.NotRun);

        gate([clean]).ExitCode.ShouldBe(0);
        gate([clean, flaky]).ExitCode.ShouldBe(0);
        gate([clean, flaky, failed]).ExitCode.ShouldBe(1);
        gate([clean, failed, crashed]).ExitCode.ShouldBe(2);
        gate([clean, threw]).ExitCode.ShouldBe(2);
        gate([clean], "preflight failed").ExitCode.ShouldBe(2);
        gate([]).ExitCode.ShouldBe(0);
    }

    [Fact]
    public void the_report_names_every_unit_with_its_verdict_and_duration()
    {
        var clean = new GateUnitResult(unit("Clean"), results(("t", WorkerTestState.Passed, 1)), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(10), 2, null);
        var failed = new GateUnitResult(unit("Failed"), results(("t", WorkerTestState.Failed, 1)), TimeSpan.FromMinutes(2), null, 1, null);
        var notRun = new GateUnitResult(unit("NotRun"), null, TimeSpan.Zero, null, 0, null);

        var text = gate([clean, failed, notRun]).ToText();

        text.ShouldContain("✓ Clean.net10.0");
        text.ShouldContain("12s (predicted 10s)");
        text.ShouldContain("✗ Failed.net10.0");
        text.ShouldContain("2m00s (first run)");
        text.ShouldContain("- NotRun.net10.0");
        text.ShouldContain("=== Failed.net10.0 ===");   // the full report, for the unit that was not clean
        text.ShouldNotContain("=== Clean.net10.0 ===");
        // 2, not 1: a unit that never ran is unknown, and unknown outranks red.
        text.ShouldContain("Exit code 2: 1 clean, 0 passed on retry, 1 failed, 0 indeterminate, 1 not run.");
    }

    private static GateResults gate(IReadOnlyList<GateUnitResult> units, string? abortReason = null)
        => new()
        {
            Units = units,
            Preflight = [],
            Plan = units.Select(u => u.Unit).ToList(),
            AbortReason = abortReason,
            WorkerBudget = 4,
            Duration = TimeSpan.FromMinutes(3)
        };
}
