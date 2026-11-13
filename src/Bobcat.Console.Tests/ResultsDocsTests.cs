using System.Text.Json;
using System.Text.Json.Nodes;
using Bobcat.Console.LivingDocs;
using Bobcat.Engine;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#553: <c>bobcat results-docs</c> writes living documentation from results files — one
/// Markdown page per specification, an index per feature and per run, the folded results and the
/// web component — deterministically, over a results directory these tests write.
/// </summary>
public class ResultsDocsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bobcat-results-docs", Guid.NewGuid().ToString("N"));
    private string results => Path.Combine(_root, "results");
    private string docs => Path.Combine(_root, "site", "specs");

    public ResultsDocsTests()
    {
        Directory.CreateDirectory(results);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static StepRender step(string keyword, string text, ResultStatus status = ResultStatus.success)
        => new() { StepId = "s", Keyword = keyword, Kind = StepRender.KindOf(keyword), StepText = text, Status = status };

    private static SpecRender spec(string feature, string title, bool passed, params StepRender[] steps)
        => new()
        {
            Title = title, FeatureTitle = feature, Succeeded = passed, Counts = new Counts(passed ? 1 : 0, passed ? 0 : 1, 0), DurationMs = 12,
            Steps = steps.Length > 0 ? steps.ToList() : [step("Then", $"{title} holds", passed ? ResultStatus.success : ResultStatus.failed)]
        };

    /// <summary>
    /// A results file in the directory, in the shape the run writes (each scenario through
    /// <see cref="JsonRenderer.RenderScenario"/>, the file's own records being internal to core),
    /// finished <paramref name="minute"/> minutes in.
    /// </summary>
    private string file(string name, int minute, params SpecRender[] specs)
    {
        var started = new DateTimeOffset(2026, 10, 16, 19, 31, 9, TimeSpan.Zero).AddMinutes(minute);
        var scenarios = new JsonArray();
        foreach (var spec in specs.OrderBy(x => x.FeatureTitle, StringComparer.Ordinal).ThenBy(x => x.Title, StringComparer.Ordinal))
        {
            scenarios.Add(JsonNode.Parse(JsonRenderer.RenderScenario(spec)));
        }

        var document = new JsonObject
        {
            ["$schema"] = ResultsFile.SchemaFileName,
            ["schemaVersion"] = ResultsFile.SchemaVersion,
            ["run"] = new JsonObject
            {
                ["runId"] = "b4c8ddd9-c268-4af8-87ec-c4f7f30b4c81", ["suite"] = "My.Specs", ["mode"] = "xunit", ["repository"] = "/repo", ["branch"] = "main",
                ["startedAt"] = started.ToString("o"), ["finishedAt"] = started.AddSeconds(5).ToString("o"), ["durationMs"] = 5000
            },
            ["summary"] = new JsonObject { ["scenarios"] = specs.Length, ["passed"] = specs.Count(x => x.Succeeded), ["failed"] = specs.Count(x => !x.Succeeded), ["succeeded"] = specs.All(x => x.Succeeded) },
            ["scenarios"] = scenarios
        };

        var path = Path.Combine(results, name);
        File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string read(string path) => File.ReadAllText(path);

    [Fact]
    public void one_page_per_specification_an_index_per_feature_and_one_for_the_run()
    {
        var path = file("My.Specs-run.json", 0,
            spec("Loan Underwriting", "the underwriting rules", true),
            spec("Loan Underwriting", "a declined application", false),
            spec("Accounts", "opens an account", true));

        var outcome = ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: "customElements.define('bobcat-spec', X)");

        outcome.Features.ShouldBe(2);
        outcome.Specifications.ShouldBe(3);
        Directory.EnumerateFiles(docs, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(docs, f).Replace('\\', '/')).Order().ShouldBe(
        [
            "accounts/index.md",
            "accounts/opens-an-account.md",
            "bobcat-spec.js",
            "index.md",
            "loan-underwriting/a-declined-application.md",
            "loan-underwriting/index.md",
            "loan-underwriting/the-underwriting-rules.md",
            "results.json"
        ]);

        var page = read(Path.Combine(docs, "loan-underwriting", "the-underwriting-rules.md"));
        page.ShouldStartWith(ResultsDocs.Marker + "\n\n# the underwriting rules\n\n**Feature:** [Loan Underwriting](index.md)\n\n**✓ passed**");
        page.ShouldNotContain("attempt");

        var feature = read(Path.Combine(docs, "loan-underwriting", "index.md"));
        feature.ShouldContain("# Loan Underwriting\n\n[All specifications](../index.md)\n\n");
        feature.ShouldContain("- ✗ [a declined application](a-declined-application.md) — failed · 12ms\n");
        feature.ShouldContain("- ✓ [the underwriting rules](the-underwriting-rules.md) — passed · 12ms\n");

        var index = read(Path.Combine(docs, "index.md"));
        index.ShouldContain("# Specifications\n\n**Suite:** My.Specs · **Run started:** 2026-10-16 19:31:09 UTC · **Branch:** main\n\n3 specifications: 2 passed, 1 failed\n\n");
        index.ShouldContain("| [Accounts](accounts/index.md) | 1 | 1 | 0 | 0 |\n| [Loan Underwriting](loan-underwriting/index.md) | 2 | 1 | 1 | 0 |\n");

        read(Path.Combine(docs, "bobcat-spec.js")).ShouldBe("customElements.define('bobcat-spec', X)");

        var folded = JsonDocument.Parse(read(Path.Combine(docs, "results.json"))).RootElement;
        var one = folded.GetProperty("files").EnumerateArray().ShouldHaveSingleItem();
        one.GetProperty("attachmentBase").GetString().ShouldBe("../../results/");
        one.GetProperty("results").GetProperty("scenarios").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public void writing_twice_over_the_same_results_changes_nothing()
    {
        var path = file("My.Specs-run.json", 0, spec("Orders", "places an order", true), spec("Orders", "refuses an empty order", false));

        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: "x");
        var first = Directory.EnumerateFiles(docs, "*", SearchOption.AllDirectories).Order().ToDictionary(f => f, File.ReadAllBytes);
        var stamps = first.Keys.ToDictionary(f => f, File.GetLastWriteTimeUtc);

        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: "x");
        var second = Directory.EnumerateFiles(docs, "*", SearchOption.AllDirectories).Order().ToDictionary(f => f, File.ReadAllBytes);

        second.Keys.ShouldBe(first.Keys);
        foreach (var (name, bytes) in second)
        {
            bytes.ShouldBe(first[name], name);
            // Unchanged content is not rewritten, so a documentation build watching the tree sees nothing.
            File.GetLastWriteTimeUtc(name).ShouldBe(stamps[name], name);
        }
    }

    [Fact]
    public void a_retried_specification_shows_its_latest_attempt_first_and_the_earlier_ones_below()
    {
        // A supervised run: one file per worker request, the retry's finished later.
        file("My.Specs-run.json", 0, spec("Orders", "places an order", false));
        file("My.Specs-run-2.json", 1, spec("Orders", "places an order", true));

        ResultsDocs.Write(Bobcat.Ui.ResultsPages.ResultsPage.ResultsFilesIn([results]), docs, attachmentsKept: true, embedScript: null);

        var page = read(Path.Combine(docs, "orders", "places-an-order.md"));
        page.ShouldContain("**✓ passed** · Rights: 1, Wrongs: 0, Errors: 0 · 12ms · attempt 2 of 2\n");
        page.ShouldContain("## Earlier attempts\n\n### Attempt 1 of 2 — ✗ failed\n\n**Feature:** [Orders](index.md)\n\n**✗ failed** · Rights: 0, Wrongs: 1, Errors: 0 · 12ms · attempt 1 of 2\n");
        page.IndexOf("attempt 2 of 2", StringComparison.Ordinal).ShouldBeLessThan(page.IndexOf("attempt 1 of 2", StringComparison.Ordinal));

        read(Path.Combine(docs, "orders", "index.md")).ShouldContain("— passed · 12ms · 2 attempts\n");
        read(Path.Combine(docs, "index.md")).ShouldContain("**Results files:** 2");
        read(Path.Combine(docs, "index.md")).ShouldContain("1 specification: 1 passed, 0 failed\n");
    }

    [Fact]
    public void stale_pages_this_command_wrote_are_removed_and_other_files_are_left_alone()
    {
        var path = file("My.Specs-run.json", 0, spec("Orders", "places an order", true), spec("Orders", "renamed since", true));
        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: null);
        File.Exists(Path.Combine(docs, "orders", "renamed-since.md")).ShouldBeTrue();

        // Somebody's own page beside the generated ones, and a generated-looking directory of theirs.
        var own = Path.Combine(docs, "orders", "notes.md");
        File.WriteAllText(own, "# My notes\n");
        var elsewhere = Path.Combine(docs, "guide.md");
        File.WriteAllText(elsewhere, "# Guide\n");

        File.Delete(path);
        var again = file("My.Specs-run2.json", 1, spec("Orders", "places an order", true), spec("Shipping", "ships an order", true));
        var outcome = ResultsDocs.Write([again], docs, attachmentsKept: true, embedScript: null);

        outcome.Removed.Select(Path.GetFileName).ShouldBe(["renamed-since.md"]);
        File.Exists(own).ShouldBeTrue();
        File.Exists(elsewhere).ShouldBeTrue();
        File.Exists(Path.Combine(docs, "shipping", "ships-an-order.md")).ShouldBeTrue();
        outcome.Describe().ShouldEndWith("(1 results file, 1 stale page removed)");
    }

    [Fact]
    public void a_feature_whose_pages_all_went_leaves_no_directory()
    {
        var path = file("My.Specs-run.json", 0, spec("Orders", "places an order", true), spec("Shipping", "ships an order", true));
        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: null);

        File.Delete(path);
        ResultsDocs.Write([file("My.Specs-run2.json", 1, spec("Orders", "places an order", true))], docs, attachmentsKept: true, embedScript: null);

        Directory.Exists(Path.Combine(docs, "shipping")).ShouldBeFalse();
    }

    [Fact]
    public void attachments_link_into_the_results_directory_relative_to_the_page_or_not_at_all()
    {
        var withAttachment = spec("Orders", "a screenshot is taken", false, new StepRender
        {
            Keyword = "Then", Kind = StepKind.Then, StepText = "the page is wrong", Status = ResultStatus.failed,
            Attachments = [new AttachmentRender { Name = "screen.png", MediaType = "image/png", Size = 1024, Path = "attachments/run/spec/01-screen.png" }]
        });
        var path = file("My.Specs-run.json", 0, withAttachment);

        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: null);
        read(Path.Combine(docs, "orders", "a-screenshot-is-taken.md"))
            .ShouldContain("![screen.png](../../../results/attachments/run/spec/01-screen.png)");

        ResultsDocs.Write([path], docs, attachmentsKept: false, embedScript: null);
        var page = read(Path.Combine(docs, "orders", "a-screenshot-is-taken.md"));
        page.ShouldContain("📎 screen.png (image/png, 1 KB) — not kept with these results");
        page.ShouldNotContain("![");
        JsonDocument.Parse(read(Path.Combine(docs, "results.json"))).RootElement
            .GetProperty("files")[0].GetProperty("attachmentBase").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("the underwriting rules", "the-underwriting-rules")]
    [InlineData("  Loan   Underwriting! ", "loan-underwriting")]
    [InlineData("adds (a: 1, b: 2)", "adds-a-1-b-2")]
    [InlineData("Déjà vu über alles", "déjà-vu-über-alles")]
    [InlineData("***", "spec")]
    public void a_slug_is_lower_case_letters_and_digits_joined_by_dashes(string title, string slug)
    {
        ResultsDocs.Slug(title).ShouldBe(slug);
    }

    [Fact]
    public void two_titles_with_one_slug_get_numbered_in_order()
    {
        ResultsDocs.Slugs(["Adds up", "adds up", "adds-up", "other"]).ShouldBe(["adds-up", "adds-up-2", "adds-up-3", "other"]);
    }

    [Fact]
    public void a_data_rows_identity_keeps_its_row_part()
    {
        var path = file("My.Specs-run.json", 0,
            spec("Calculator", "adds (a: 1, b: 2)", true),
            spec("Calculator", "adds (a: 3, b: 4)", true),
            spec("Calculator", "adds", true));

        ResultsDocs.Write([path], docs, attachmentsKept: true, embedScript: null);

        Directory.EnumerateFiles(Path.Combine(docs, "calculator"), "*.md").Select(Path.GetFileName).Order().ShouldBe(
            ["adds-a-1-b-2.md", "adds-a-3-b-4.md", "adds.md", "index.md"]);
        read(Path.Combine(docs, "calculator", "adds-a-1-b-2.md")).ShouldContain("# adds (a: 1, b: 2)\n");
    }

    [Fact]
    public async Task the_tool_writes_the_documentation_for_a_results_directory()
    {
        file("My.Specs-run.json", 0, spec("Orders", "places an order", true), spec("Orders", "refuses an empty order", false));
        File.WriteAllText(Path.Combine(results, ResultsFile.SchemaFileName), ResultsFile.SchemaText);

        var result = await Tool.Run("results-docs", results, "--out", docs, "--no-script");

        result.ExitCode.ShouldBe(0, result.Output + result.Error);
        result.Output.ShouldContain($"Wrote 2 specifications in 1 feature to {docs} (1 results file)");
        File.Exists(Path.Combine(docs, "orders", "refuses-an-empty-order.md")).ShouldBeTrue();
        File.Exists(Path.Combine(docs, "index.md")).ShouldBeTrue();
        File.Exists(Path.Combine(docs, "results.json")).ShouldBeTrue();
        File.Exists(Path.Combine(docs, "bobcat-spec.js")).ShouldBeFalse();
    }

    [Fact]
    public async Task the_tool_defaults_to_a_docs_directory_under_the_results()
    {
        file("My.Specs-run.json", 0, spec("Orders", "places an order", true));

        var result = await Tool.Run("results-docs", results, "--no-script");

        result.ExitCode.ShouldBe(0, result.Output + result.Error);
        File.Exists(Path.Combine(results, "docs", "orders", "places-an-order.md")).ShouldBeTrue();
    }

    [Fact]
    public async Task a_missing_results_file_is_said_on_standard_error()
    {
        var result = await Tool.Run("results-docs", Path.Combine(_root, "nowhere"), "--out", docs);

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("No such file or directory");
        Directory.Exists(docs).ShouldBeFalse();
    }

    [Fact]
    public async Task a_directory_with_no_results_files_is_refused_with_the_way_to_write_one()
    {
        var result = await Tool.Run("results-docs", results, "--out", docs);

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("No results files in");
        result.Error.ShouldContain("BOBCAT_RESULTS");
    }

    [Fact]
    public async Task a_file_that_is_not_a_results_file_is_refused_by_name()
    {
        var other = Path.Combine(results, "other.json");
        File.WriteAllText(other, """{"run":{}}""");

        var result = await Tool.Run("results-docs", other, "--out", docs);

        result.ExitCode.ShouldBe(1);
        result.Error.ShouldContain("is not a Bobcat results file");
    }
}
