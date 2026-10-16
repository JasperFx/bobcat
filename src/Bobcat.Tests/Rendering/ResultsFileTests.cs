using System.Text.Json;
using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// Issue #540: one JSON results file per run, with a JSON Schema generated from its records.
/// In the marker-step-run collection because the collector is process-wide and a parallel test's
/// scenarios would land in it.
/// </summary>
[Collection("marker-step-run")]
public class ResultsFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bobcat-results-tests", Guid.NewGuid().ToString("N"));

    private static MonitorRunInfo run(string suite = "My.Specs")
        => new(Guid.NewGuid(), suite, "/repo", "main", "xunit") { Tag = "node-1" };

    public void Dispose()
    {
        ResultsFile.Reset();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void the_schema_written_beside_a_results_file_is_the_generated_one()
    {
        // Not committed and not embedded (bobcat#604): the copy beside a file comes straight from
        // the records, so it cannot drift from them.
        ResultsFile.BeginRun(run(), _directory);
        finish("Orders", "places an order", passed: true);
        ResultsFile.EndRun().ShouldNotBeNull();

        File.ReadAllText(Path.Combine(_directory, ResultsFile.SchemaFileName)).ShouldBe(ResultsFile.GenerateSchema());
        ResultsFile.SchemaText.ShouldBe(ResultsFile.GenerateSchema());
    }

    [Fact]
    public void the_schema_is_a_draft_2020_12_schema_requiring_nothing_it_does_not_describe()
    {
        var schema = JsonDocument.Parse(ResultsFile.SchemaText).RootElement;

        schema.GetProperty("$schema").GetString().ShouldBe("https://json-schema.org/draft/2020-12/schema");
        var properties = schema.GetProperty("properties");
        foreach (var name in new[] { "$schema", "schemaVersion", "run", "summary", "scenarios", "timing" })
        {
            properties.TryGetProperty(name, out _).ShouldBeTrue(name);
        }
    }

    [Fact]
    public void a_bracket_writes_one_file_holding_every_scenario_it_finished()
    {
        var info = run();
        ResultsFile.BeginRun(info, _directory);

        finish("Orders", "places an order", passed: true);
        finish("Orders", "refuses an empty order", passed: false);
        finish("Accounts", "opens an account", passed: true);

        var path = ResultsFile.EndRun().ShouldNotBeNull();

        Path.GetFileName(path).ShouldBe($"My.Specs-{info.RunId:N}.json");
        File.Exists(Path.Combine(_directory, ResultsFile.SchemaFileName)).ShouldBeTrue();

        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        json.GetProperty("$schema").GetString().ShouldBe(ResultsFile.SchemaFileName);
        json.GetProperty("schemaVersion").GetString().ShouldBe(ResultsFile.SchemaVersion);
        json.GetProperty("run").GetProperty("runId").GetGuid().ShouldBe(info.RunId);
        json.GetProperty("run").GetProperty("tag").GetString().ShouldBe("node-1");

        var summary = json.GetProperty("summary");
        summary.GetProperty("scenarios").GetInt32().ShouldBe(3);
        summary.GetProperty("passed").GetInt32().ShouldBe(2);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        summary.GetProperty("succeeded").GetBoolean().ShouldBeFalse();

        // Feature, then title: the console's order, so two runs of one suite diff cleanly.
        json.GetProperty("scenarios").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ShouldBe(
        [
            "Accounts/opens an account", "Orders/places an order", "Orders/refuses an empty order"
        ]);

        var failing = json.GetProperty("scenarios")[2];
        failing.GetProperty("succeeded").GetBoolean().ShouldBeFalse();
        var cell = failing.GetProperty("steps")[0].GetProperty("cells")[0];
        cell.GetProperty("expected").GetString().ShouldBe("1");
        cell.GetProperty("actual").GetString().ShouldBe("2");
    }

    [Fact]
    public void every_property_a_file_carries_is_one_the_schema_describes()
    {
        ResultsFile.BeginRun(run(), _directory);
        finish("Orders", "places an order", passed: true);
        finish("Orders", "refuses an empty order", passed: false);
        var path = ResultsFile.EndRun().ShouldNotBeNull();

        var schema = JsonDocument.Parse(ResultsFile.SchemaText).RootElement;
        var file = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        var undescribed = new List<string>();
        conforms(file, schema, schema, "", undescribed);
        undescribed.ShouldBeEmpty();
    }

    [Fact]
    public void a_failing_acceptance_spec_is_in_the_file_and_does_not_fail_the_run()
    {
        // bobcat#545 meets #540: the spec is there in full under its own verdict, counted apart, and
        // the summary's succeeded agrees with run_finished rather than calling the run failed.
        ResultsFile.BeginRun(run(), _directory);
        finish("Orders", "places an order", passed: true);
        finish("Orders", "refunds an order", passed: false, Lifecycle.Acceptance);
        finish("Orders", "ships an order", passed: true, Lifecycle.Acceptance);
        var path = ResultsFile.EndRun().ShouldNotBeNull();

        var file = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        var summary = file.GetProperty("summary");
        summary.GetProperty("scenarios").GetInt32().ShouldBe(3);
        summary.GetProperty("passed").GetInt32().ShouldBe(2);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        summary.GetProperty("acceptanceFailing").GetInt32().ShouldBe(1);
        summary.GetProperty("succeeded").GetBoolean().ShouldBeTrue();

        var verdicts = file.GetProperty("scenarios").EnumerateArray()
            .ToDictionary(x => x.GetProperty("title").GetString()!, x => x.GetProperty("verdict").GetString());
        verdicts["refunds an order"].ShouldBe("acceptance: failing");
        verdicts["ships an order"].ShouldBe("acceptance: passing");
        verdicts["places an order"].ShouldBe("passed");
    }

    [Fact]
    public void nothing_is_written_when_nobody_asked()
    {
        ResultsFile.BeginRun(run(), directory: null);
        finish("Orders", "places an order", passed: true);

        ResultsFile.EndRun().ShouldBeNull();
        Directory.Exists(_directory).ShouldBeFalse();
    }

    [Fact]
    public void a_second_file_for_the_same_run_takes_the_next_name_rather_than_overwriting()
    {
        var info = run();

        ResultsFile.BeginRun(info, _directory);
        finish("Orders", "first", passed: true);
        var first = ResultsFile.EndRun().ShouldNotBeNull();

        ResultsFile.BeginRun(info, _directory);
        finish("Orders", "second", passed: true);
        var second = ResultsFile.EndRun().ShouldNotBeNull();

        second.ShouldNotBe(first);
        Path.GetFileName(second).ShouldBe($"My.Specs-{info.RunId:N}-2.json");
        File.ReadAllText(first).ShouldContain("Orders/first");
    }

    [Fact]
    public void a_directory_that_cannot_be_written_never_fails_the_run()
    {
        // A FILE where the directory should be.
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blocked, "");

        ResultsFile.BeginRun(run(), blocked);
        finish("Orders", "places an order", passed: true);

        Should.NotThrow(() => ResultsFile.EndRun()).ShouldBeNull();
    }

    [Fact]
    public void a_passing_scenarios_reports_are_in_the_file_only_when_the_run_is_verbose()
    {
        ScenarioReportVisibility.Reset();
        try
        {
            ResultsFile.BeginRun(run(), _directory);
            finishWithReport("quiet");
            var quiet = JsonDocument.Parse(File.ReadAllText(ResultsFile.EndRun()!)).RootElement;
            quiet.GetProperty("scenarios")[0].TryGetProperty("reports", out _).ShouldBeFalse();

            ScenarioReportVisibility.Verbose = true;
            ResultsFile.BeginRun(run(), _directory);
            finishWithReport("verbose");
            var verbose = JsonDocument.Parse(File.ReadAllText(ResultsFile.EndRun()!)).RootElement;
            verbose.GetProperty("scenarios")[0].GetProperty("reports").GetArrayLength().ShouldBe(1);
        }
        finally
        {
            ScenarioReportVisibility.Reset();
        }
    }

    private static void finish(string feature, string scenario, bool passed, Lifecycle lifecycle = Lifecycle.Regression)
    {
        var recording = ScenarioRecorder.Begin(feature, scenario, null, Guid.NewGuid());
        recording.Lifecycle = lifecycle;
        using (ScenarioRecorder.Step("Then", "the count is 1"))
        {
            if (!passed)
                ScenarioRecorder.CurrentStep!.Cells.Add(new CellResult("count", ResultStatus.failed) { Expected = "1", Actual = "2" });
        }

        recording.Dispose();
    }

    private static void finishWithReport(string scenario)
    {
        var recording = ScenarioRecorder.Begin("Reports", scenario, null, Guid.NewGuid());
        using (ScenarioRecorder.Step("Then", "something happened")) { }
        SpecReport.For<Bobcat.Tests.Reporting.MessageActivityReport>().Record(0, "Sent", "PlaceOrder", "local://orders", 1);
        recording.Dispose();
    }

    /// <summary>
    /// Every object property in <paramref name="instance"/> is declared by the schema node it sits
    /// under — enough to catch a writer that drifted from its schema without a validator package.
    /// </summary>
    private static void conforms(JsonElement instance, JsonElement node, JsonElement root, string path, List<string> undescribed)
    {
        if (node.TryGetProperty("$ref", out var reference))
        {
            node = resolve(root, reference.GetString()!);
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                if (!node.TryGetProperty("properties", out var properties))
                {
                    if (!node.TryGetProperty("additionalProperties", out _)) undescribed.Add(path + " (no properties)");
                    return;
                }

                foreach (var property in instance.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var child))
                    {
                        undescribed.Add($"{path}/{property.Name}");
                        continue;
                    }

                    conforms(property.Value, child, root, $"{path}/{property.Name}", undescribed);
                }

                break;

            case JsonValueKind.Array:
                if (!node.TryGetProperty("items", out var items))
                {
                    undescribed.Add(path + " (no items)");
                    return;
                }

                var index = 0;
                foreach (var item in instance.EnumerateArray()) conforms(item, items, root, $"{path}[{index++}]", undescribed);
                break;
        }
    }

    private static JsonElement resolve(JsonElement root, string reference)
    {
        var node = root;
        foreach (var segment in reference.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.GetProperty(segment.Replace("~1", "/").Replace("~0", "~"));
        }

        return node;
    }
}
