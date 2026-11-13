using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// bobcat#553: a results file (bobcat#540) reads back into the <see cref="SpecRender"/> it was written
/// from, so what renders from the file is what rendered from the run.
/// </summary>
public class ResultsFileReaderTests
{
    private static string fileOf(params SpecRender[] specs)
    {
        var run = new MonitorRunInfo(Guid.Parse("b4c8ddd9-c268-4af8-87ec-c4f7f30b4c81"), "My.Specs", "/repo", "main", "xunit") { Tag = "node-1" };
        var started = new DateTimeOffset(2026, 10, 16, 19, 31, 9, TimeSpan.Zero);
        return ResultsFile.Serialize(ResultsFile.Build(run, started, started.AddMilliseconds(227), specs, null));
    }

    private static SpecRender everything()
    {
        var gridCells = new List<CellResult>
        {
            new("Name", ResultStatus.success) { Expected = "Jeremy", Actual = "Jeremy", RowIndex = 0 },
            new("missing-row", ResultStatus.missing) { Note = "Missing row: Bob", RowIndex = 1 },
            new("Name", ResultStatus.missing) { Expected = "Bob", RowIndex = 1 }
        };
        var reportCells = new List<CellResult> { new("Message", ResultStatus.ok, "OrderPlaced") { RowIndex = 0 } };

        return new SpecRender
        {
            Title = "refuses an empty order",
            FeatureTitle = "Orders",
            Succeeded = false,
            Counts = new Counts(1, 2, 1),
            DurationMs = 120,
            StepsMs = 90,
            Steps =
            [
                new StepRender { StepId = "d1", Keyword = "Given", Kind = StepKind.Given, IsNarrative = true, StepText = "an empty cart", Status = ResultStatus.success },
                new StepRender
                {
                    StepId = "s1", Keyword = "When", Kind = StepKind.When, Depth = 1, StepText = "the order is placed", Status = ResultStatus.success,
                    StartedAtMs = 2, DurationMs = 40, Logs = ["posting"], SuppressedLogs = 2, Diagnostics = { ["status"] = "400" }
                },
                new StepRender
                {
                    StepId = "s2", Keyword = "Then", Kind = StepKind.Then, StepText = "it is refused", Status = ResultStatus.failed,
                    ErrorMessage = "no refusal", FailureLevel = FailureLevel.Assertion,
                    Cells = [CellRender.From(new CellResult("status", ResultStatus.failed) { Expected = "400", Actual = "200" })],
                    Attachments = [new AttachmentRender { Name = "body.json", MediaType = "application/json", Size = 12, Path = "attachments/r/s/01-body.json" }]
                },
                new StepRender
                {
                    StepId = "s3", Keyword = "And", Kind = StepKind.Then, StepText = "the names are", Status = ResultStatus.failed,
                    Cells = gridCells.Select(CellRender.From).ToList(), SetVerification = SetVerificationRender.FromCells(["Name"], gridCells)
                },
                new StepRender
                {
                    StepId = "s4", Keyword = "And", Kind = StepKind.Then, StepText = "the broker is asked", Status = ResultStatus.error,
                    ErrorMessage = "down", ExceptionType = "InvalidOperationException", FailureLevel = FailureLevel.Critical
                },
                new StepRender { StepId = "p9", Keyword = "Then", Kind = StepKind.Then, StepText = "it is logged", Status = ResultStatus.ok, NotRun = true }
            ],
            Logs = ["between"],
            SuppressedLogs = 1,
            Reports = [new ReportRender { Title = "Message activity", ShortTitle = "Messages", SuppressedRows = 4, Grid = SetVerificationRender.FromCells(["Message"], reportCells) }],
            Attachments = [new AttachmentRender { Name = "dump.bin", MediaType = "application/octet-stream", Size = 5, Omitted = "too big" }],
            SuppressedAttachments = 1
        };
    }

    [Fact]
    public void the_run_is_read_back()
    {
        var contents = ResultsFileReader.Read(fileOf(everything()), "/ci/My.Specs-abc.json");

        contents.Path.ShouldBe("/ci/My.Specs-abc.json");
        contents.SchemaVersion.ShouldBe(ResultsFile.SchemaVersion);
        contents.RunId.ShouldBe(Guid.Parse("b4c8ddd9-c268-4af8-87ec-c4f7f30b4c81"));
        contents.Suite.ShouldBe("My.Specs");
        contents.Mode.ShouldBe("xunit");
        contents.Branch.ShouldBe("main");
        contents.Tag.ShouldBe("node-1");
        contents.DurationMs.ShouldBe(227);
        contents.Scenarios.Count.ShouldBe(1);
    }

    [Fact]
    public void a_scenario_renders_the_same_from_the_file_as_it_did_from_the_run()
    {
        var spec = everything();

        var read = ResultsFileReader.Read(fileOf(spec)).Scenarios.ShouldHaveSingleItem();

        // Three renderers, one model: the plain text, the Markdown and the JSON of the read-back
        // scenario are the run's own.
        PlainSpecText.Render(read).ShouldBe(PlainSpecText.Render(spec));
        MarkdownSpecText.Render(read).ShouldBe(MarkdownSpecText.Render(spec));
        JsonRenderer.RenderScenario(read).ShouldBe(JsonRenderer.RenderScenario(spec));

        ResultsFileReader.IdentityOf(read).ShouldBe("Orders/refuses an empty order");
        read.Steps[1].SuppressedLogs.ShouldBe(2);
        read.Steps[2].Attachments.ShouldHaveSingleItem().Path.ShouldBe("attachments/r/s/01-body.json");
        read.Steps[3].SetVerification.ShouldNotBeNull().Rows[1].RowType.ShouldBe(SetVerificationRowType.Missing);
        read.Reports.ShouldHaveSingleItem().SuppressedRows.ShouldBe(4);
    }

    [Fact]
    public void the_acceptance_verdicts_read_back_as_themselves()
    {
        var failing = new SpecRender { Title = "a", FeatureTitle = "F", Succeeded = false, Lifecycle = Lifecycle.Acceptance, AcceptanceFailing = true };
        var ready = new SpecRender { Title = "b", FeatureTitle = "F", Succeeded = true, Lifecycle = Lifecycle.Acceptance };

        var read = ResultsFileReader.Read(fileOf(failing, ready)).Scenarios;

        read[0].Verdict.ShouldBe("acceptance: failing");
        read[0].FailsTheRun.ShouldBeFalse();
        read[1].Verdict.ShouldBe("acceptance: passing");
        read[1].ReadyToPromote.ShouldBeTrue();
    }

    [Fact]
    public void a_scenario_with_no_feature_has_its_title_as_identity()
    {
        var read = ResultsFileReader.Read(fileOf(new SpecRender { Title = "alone", Succeeded = true })).Scenarios.ShouldHaveSingleItem();

        read.FeatureTitle.ShouldBeNull();
        ResultsFileReader.IdentityOf(read).ShouldBe("alone");
    }

    [Fact]
    public void an_older_file_with_fewer_properties_still_reads()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "run": { "runId": "b4c8ddd9-c268-4af8-87ec-c4f7f30b4c81", "suite": "Old.Specs" },
              "scenarios": [
                { "title": "adds", "feature": "Calculator", "succeeded": true, "verdict": "passed",
                  "steps": [ { "kind": "Then", "text": "the sum is 6", "status": "success", "cells": [ { "column": "sum", "status": "success", "expected": "6", "actual": "6" } ] } ],
                  "somethingNewer": { "ignored": true } }
              ]
            }
            """;

        var contents = ResultsFileReader.Read(json);

        contents.Suite.ShouldBe("Old.Specs");
        var spec = contents.Scenarios.ShouldHaveSingleItem();
        spec.Succeeded.ShouldBeTrue();
        spec.Counts.Rights.ShouldBe(0);
        spec.Steps.ShouldHaveSingleItem().Cells.ShouldHaveSingleItem().DisplayText.ShouldBe("6");
    }

    [Fact]
    public void a_file_that_is_not_a_results_file_is_refused_by_name()
    {
        Should.Throw<InvalidDataException>(() => ResultsFileReader.Read("""{"run":{}}""", "/ci/other.json"))
            .Message.ShouldStartWith("/ci/other.json is not a Bobcat results file");

        Should.Throw<InvalidDataException>(() => ResultsFileReader.Read("[]"));
        Should.Throw<System.Text.Json.JsonException>(() => ResultsFileReader.Read("not json"));
    }
}
