using Bobcat.Engine;
using Bobcat.Monitoring;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// bobcat#553: one scenario as GitHub-flavoured Markdown, from the same <see cref="SpecRender"/> the
/// console, the JSON and the plain text render, with the same words.
/// </summary>
public class MarkdownSpecTextTests
{
    private static CellRender cell(string name, ResultStatus status, string? expected = null, string? actual = null, int row = -1, string? note = null)
        => CellRender.From(new CellResult(name, status) { Expected = expected, Actual = actual, Note = note, RowIndex = row });

    private static StepRender narrative(string keyword, string text, ResultStatus status = ResultStatus.ok)
        => new() { StepId = "d1", Keyword = keyword, Kind = StepRender.KindOf(keyword), IsNarrative = true, StepText = text, Status = status };

    private static StepRender step(string keyword, string text, ResultStatus status = ResultStatus.success, int depth = 0, long ms = 0)
        => new() { StepId = "s" + text.Length, Keyword = keyword, Kind = StepRender.KindOf(keyword), Depth = depth, StepText = text, Status = status, DurationMs = ms };

    private static SpecRender passing() => new()
    {
        Title = "adds two numbers",
        FeatureTitle = "Calculator",
        Succeeded = true,
        Counts = new Counts(1, 0, 0),
        DurationMs = 39,
        StepsMs = 35,
        Steps =
        [
            narrative("Given", "a calculator"),
            step("When", "I add 2 and 4", ms: 3),
            new StepRender
            {
                StepId = "s2", Keyword = "Then", Kind = StepKind.Then, StepText = "the sum should be 6",
                Status = ResultStatus.success, Cells = [cell("sum", ResultStatus.success, "6", "6")]
            }
        ]
    };

    [Fact]
    public void a_passing_scenario_is_a_heading_a_verdict_line_and_a_step_list()
    {
        MarkdownSpecText.Render(passing()).ShouldBe(
            """
            # adds two numbers

            **Feature:** Calculator

            **✓ passed** · Rights: 1, Wrongs: 0, Errors: 0 · 39ms (steps 35ms)

            - · **Given** a calculator — *declared*
            - ✓ **When** I add 2 and 4 *(3ms)*
            - ✓ **Then** the sum should be 6
              - ✓ sum: 6

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void the_heading_level_the_feature_link_and_the_attempt_are_options()
    {
        var markdown = MarkdownSpecText.Render(passing(), new MarkdownSpecOptions
        {
            HeadingLevel = 3, FeatureLink = "index.md", Attempt = 2, Attempts = 2
        });

        markdown.ShouldStartWith("### adds two numbers\n\n**Feature:** [Calculator](index.md)\n\n");
        markdown.ShouldContain("· 39ms (steps 35ms) · attempt 2 of 2\n");

        MarkdownSpecText.Render(passing(), new MarkdownSpecOptions { IncludeHeading = false })
            .ShouldStartWith("**Feature:** Calculator");
    }

    [Fact]
    public void a_failing_cell_reads_as_the_console_phrases_it()
    {
        var spec = new SpecRender
        {
            Title = "adds two numbers", FeatureTitle = "Calculator", Succeeded = false, Counts = new Counts(0, 1, 0),
            Steps =
            [
                new StepRender
                {
                    Keyword = "Then", Kind = StepKind.Then, StepText = "the sum should be 6", Status = ResultStatus.failed,
                    Cells = [cell("sum", ResultStatus.failed, "6", "8")]
                }
            ]
        };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain("**✗ failed** · Rights: 0, Wrongs: 1, Errors: 0\n");
        markdown.ShouldContain("- ✗ **Then** the sum should be 6\n  - ✗ sum: expected '6', got '8'\n");
    }

    [Fact]
    public void a_judged_grid_is_a_table_with_a_verdict_column()
    {
        var cells = new List<CellResult>
        {
            new("Name", ResultStatus.success) { Expected = "Jeremy", Actual = "Jeremy", RowIndex = 0 },
            new("Age", ResultStatus.success) { Expected = "51", Actual = "51", RowIndex = 0 },
            new("Name", ResultStatus.success) { Expected = "Bob", Actual = "Bob", RowIndex = 1 },
            new("Age", ResultStatus.failed) { Expected = "40", Actual = "41", RowIndex = 1 }
        };
        var spec = new SpecRender
        {
            Title = "the people are", Succeeded = false,
            Steps =
            [
                new StepRender
                {
                    Keyword = "Then", Kind = StepKind.Then, StepText = "the people are", Status = ResultStatus.failed,
                    Cells = cells.Select(CellRender.From).ToList(),
                    SetVerification = SetVerificationRender.FromCells(["Name", "Age"], cells)
                }
            ]
        };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain(
            """
            - ✗ **Then** the people are

              | # | Name | Age | Verdict |
              |---|---|---|---|
              | 1 | Jeremy | 51 | OK |
              | 2 | Bob | expected '40', got '41' | FAIL |

            """.ReplaceLineEndings("\n"));

        // The grid already showed every row's cells: none is listed again underneath.
        markdown.ShouldNotContain("- ✗ Age:");
    }

    [Fact]
    public void an_unjudged_grid_is_data_and_has_no_verdict_column()
    {
        var table = SetVerificationRender.FromTable(new StepTable(["Name", "Age"], [["Jeremy", "51"], ["Bob", "40"]]));
        var spec = new SpecRender
        {
            Title = "the people are", Succeeded = true,
            Steps = [new StepRender { Keyword = "Given", Kind = StepKind.Given, StepText = "the people are", Status = ResultStatus.success, SetVerification = table }]
        };

        MarkdownSpecText.Render(spec).ShouldContain(
            """
              | # | Name | Age |
              |---|---|---|
              | 1 | Jeremy | 51 |
              | 2 | Bob | 40 |
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void missing_and_extra_rows_are_marked_with_their_values()
    {
        var cells = new List<CellResult>
        {
            new("Name", ResultStatus.success) { Expected = "Jeremy", Actual = "Jeremy", RowIndex = 0 },
            new("missing-row", ResultStatus.missing) { Note = "Missing row: Bob", RowIndex = 1 },
            new("Name", ResultStatus.missing) { Expected = "Bob", RowIndex = 1 },
            new("extra-row", ResultStatus.invalid) { Note = "Extra row: Alice", RowIndex = 2 },
            new("Name", ResultStatus.invalid) { Actual = "Alice", RowIndex = 2 }
        };
        var spec = new SpecRender
        {
            Title = "the names are", Succeeded = false,
            Steps =
            [
                new StepRender
                {
                    Keyword = "Then", Kind = StepKind.Then, StepText = "the names are", Status = ResultStatus.failed,
                    Cells = cells.Select(CellRender.From).ToList(),
                    SetVerification = SetVerificationRender.FromCells(["Name"], cells)
                }
            ]
        };

        MarkdownSpecText.Render(spec).ShouldContain(
            """
              | # | Name | Verdict |
              |---|---|---|
              | 1 | Jeremy | OK |
              | 2 | Bob | MISSING |
              | 3 | Alice | EXTRA |
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void an_exception_is_marked_on_the_step_and_detailed_in_a_fenced_block_below()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("the broker is down");
        }
        catch (Exception e)
        {
            thrown = e;
        }

        var spec = new SpecRender
        {
            Title = "the act throws", Succeeded = false, Counts = new Counts(0, 0, 1),
            Steps =
            [
                new StepRender
                {
                    Keyword = "When", Kind = StepKind.When, StepText = "the order is placed", Status = ResultStatus.error,
                    ErrorMessage = "the broker is down", ExceptionType = "InvalidOperationException", Exception = thrown
                },
                new StepRender { Keyword = "Then", Kind = StepKind.Then, StepText = "it is confirmed", Status = ResultStatus.ok, NotRun = true }
            ]
        };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain("- ! **When** the order is placed\n  - ! InvalidOperationException — see below\n");
        markdown.ShouldContain("- ○ **Then** it is confirmed — *not run*\n");
        markdown.ShouldContain("**the order is placed**\n\n```\nInvalidOperationException: the broker is down\n  at ");
        markdown.ShouldContain("MarkdownSpecTextTests");
    }

    [Fact]
    public void an_exception_read_back_from_a_file_has_its_type_and_message_and_no_stack()
    {
        var spec = new SpecRender
        {
            Title = "the act throws", Succeeded = false,
            Steps =
            [
                new StepRender
                {
                    Keyword = "When", Kind = StepKind.When, StepText = "the order is placed", Status = ResultStatus.error,
                    ErrorMessage = "the broker is down", ExceptionType = "InvalidOperationException"
                }
            ]
        };

        MarkdownSpecText.Render(spec).ShouldContain("**the order is placed**\n\n```\nInvalidOperationException: the broker is down\n```\n");
    }

    [Fact]
    public void attachments_link_relative_to_the_page_and_images_are_shown()
    {
        var spec = new SpecRender
        {
            Title = "a screenshot is taken", Succeeded = false,
            Steps =
            [
                new StepRender
                {
                    Keyword = "Then", Kind = StepKind.Then, StepText = "the page is wrong", Status = ResultStatus.failed,
                    Attachments =
                    [
                        new AttachmentRender { Name = "screen.png", MediaType = "image/png", Size = 12 * 1024, Path = "attachments/run/the spec/01-screen.png" },
                        new AttachmentRender { Name = "body.json", MediaType = "application/json", Size = 300, Path = "attachments/run/the spec/02-body.json" }
                    ]
                }
            ],
            Attachments = [new AttachmentRender { Name = "dump.bin", MediaType = "application/octet-stream", Size = 20_000_000, Omitted = "over the 10 MB cap" }],
            SuppressedAttachments = 2
        };

        var linked = MarkdownSpecText.Render(spec, new MarkdownSpecOptions { AttachmentBase = "../results/" });
        linked.ShouldContain("  - 📎 [screen.png](../results/attachments/run/the%20spec/01-screen.png) (image/png, 12 KB)\n\n    ![screen.png](../results/attachments/run/the%20spec/01-screen.png)\n");
        linked.ShouldContain("  - 📎 [body.json](../results/attachments/run/the%20spec/02-body.json) (application/json, 300 B)\n");
        linked.ShouldContain("- 📎 dump.bin (application/octet-stream, 19.1 MB) — over the 10 MB cap\n- …and 2 more attachments not kept\n");

        // The results directory was not kept: named, linked to nothing.
        var unlinked = MarkdownSpecText.Render(spec, new MarkdownSpecOptions { AttachmentBase = null });
        unlinked.ShouldContain("  - 📎 screen.png (image/png, 12 KB) — not kept with these results\n");
        unlinked.ShouldNotContain("![");
    }

    [Fact]
    public void step_and_scenario_logs_are_fenced_with_the_capped_line()
    {
        var spec = new SpecRender
        {
            Title = "custom logging", Succeeded = false,
            Steps =
            [
                new StepRender
                {
                    Keyword = "When", Kind = StepKind.When, StepText = "the batch runs", Status = ResultStatus.success,
                    Logs = ["batch started", "batch finished"], SuppressedLogs = 3
                }
            ],
            Logs = ["between steps: 12 rows"],
            SuppressedLogs = 1
        };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain("- ✓ **When** the batch runs\n  - Logs:\n\n    ```\n    batch started\n    batch finished\n    …and 3 more lines\n    ```\n");
        markdown.ShouldContain("**Logs**\n\n```\nbetween steps: 12 rows\n…and 1 more line\n```\n");
    }

    [Fact]
    public void a_report_is_a_titled_table_and_says_what_it_left_out()
    {
        var cells = new List<CellResult>
        {
            new("Message", ResultStatus.ok, "OrderPlaced") { RowIndex = 0 },
            new("Destination", ResultStatus.ok, "rabbitmq://orders") { RowIndex = 0 }
        };
        var spec = new SpecRender
        {
            Title = "an order is placed", Succeeded = true,
            Steps = [step("When", "the order is placed")],
            Reports = [new ReportRender { Title = "Message activity", SuppressedRows = 3, Grid = SetVerificationRender.FromCells(["Message", "Destination"], cells) }]
        };

        MarkdownSpecText.Render(spec).ShouldContain(
            """
            **Message activity**

            | # | Message | Destination |
            |---|---|---|
            | 1 | OrderPlaced | rabbitmq://orders |

            …and 3 more rows not shown
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void a_failing_acceptance_specification_says_it_does_not_fail_the_build()
    {
        var spec = new SpecRender
        {
            Title = "refunds an order", FeatureTitle = "Orders", Succeeded = false, Lifecycle = Lifecycle.Acceptance, AcceptanceFailing = true,
            Counts = new Counts(1, 1, 0), Steps = [step("Then", "it is refunded", ResultStatus.failed)]
        };

        MarkdownSpecText.Render(spec).ShouldContain("**◌ acceptance: failing — not yet passing, does not fail the build** · Rights: 1, Wrongs: 1, Errors: 0\n");
    }

    [Fact]
    public void a_data_row_keeps_its_row_part_in_the_title_and_the_identity()
    {
        var spec = new SpecRender { Title = "adds (a: 1, b: 2)", FeatureTitle = "Calculator", Succeeded = true, Steps = [step("Then", "the sum is 3")] };

        MarkdownSpecText.Render(spec).ShouldStartWith("# adds (a: 1, b: 2)\n");
        ResultsFileReader.IdentityOf(spec).ShouldBe("Calculator/adds (a: 1, b: 2)");
    }

    [Fact]
    public void markdown_characters_in_a_specifications_text_are_literal()
    {
        var spec = new SpecRender
        {
            Title = "a List<int> of *stars*", Succeeded = true,
            Steps = [step("Then", "a | pipe and an [index] and snake_case")]
        };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain(@"# a List\<int\> of \*stars\*");
        markdown.ShouldContain(@"a \| pipe and an \[index\] and snake\_case");
    }

    [Fact]
    public void the_same_scenario_renders_to_the_same_bytes_in_process_and_read_back_from_its_results_file()
    {
        var spec = passing();
        var once = MarkdownSpecText.Render(spec);
        once.ShouldBe(MarkdownSpecText.Render(spec));

        var run = new MonitorRunInfo(Guid.NewGuid(), "My.Specs", "/repo", "main", "xunit");
        var json = ResultsFile.Serialize(ResultsFile.Build(run, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [spec], null));
        var read = ResultsFileReader.Read(json).Scenarios.ShouldHaveSingleItem();

        MarkdownSpecText.Render(read).ShouldBe(once);
    }

    [Fact]
    public void a_scenario_with_no_steps_is_pending_rather_than_a_clean_pass()
    {
        var spec = new SpecRender { Title = "not written yet", Succeeded = true };

        var markdown = MarkdownSpecText.Render(spec);

        markdown.ShouldContain("**✓ passed** · pending · Rights: 0");
        markdown.ShouldContain("○ this specification declares no steps");
    }
}
