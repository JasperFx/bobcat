using Bobcat.Ui.ResultsPages;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// The HTML results page (bobcat#541) is the built Vue template with a run's results files put in
/// its results element. The template only exists in a tool packed with EmbedFrontend, so these use
/// one of their own; ui-smoke.yml drives the packed tool and opens the real page in Chromium.
/// </summary>
public class ResultsPageTests
{
    private const string template = "<html><body><div id=\"app\"></div>" + ResultsPage.Placeholder + "<script type=\"module\">run()</script></body></html>";

    [Fact]
    public void the_results_files_replace_the_templates_results_element()
    {
        var html = ResultsPage.Render(template, [new ResultsPageFile("""{"summary":{"scenarios":2}}""", "results/")]);

        html.ShouldStartWith("<html><body><div id=\"app\"></div><script type=\"application/json\" id=\"bobcat-results\">");
        html.ShouldEndWith("</script><script type=\"module\">run()</script></body></html>");
        html.ShouldNotContain(ResultsPage.Placeholder);
        html.ShouldContain("""{"files":[{"attachmentBase":"results/","results":{"summary":{"scenarios":2}}}]}""");
    }

    [Fact]
    public void a_scenario_that_writes_a_closing_script_tag_cannot_end_the_element()
    {
        var html = ResultsPage.Render(template, [new ResultsPageFile("""{"title":"renders </script><script>alert(1)</script>"}""", null)]);

        // One results element and the template's own script: the scenario's text stayed data.
        html.Split("</script>").Length.ShouldBe(3);
        html.ShouldContain("\"attachmentBase\":null");
    }

    [Fact]
    public void a_template_without_its_results_element_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => ResultsPage.Render("<html></html>", []));
    }

    [Fact]
    public void a_file_that_is_not_json_is_refused()
    {
        Should.Throw<System.Text.Json.JsonException>(() => ResultsPage.Render(template, [new ResultsPageFile("not json", null)]));
    }

    [Theory]
    [InlineData("/ci/out", "/ci/out", "")]
    [InlineData("/ci/out", "/ci/out/results", "results/")]
    [InlineData("/ci/page", "/ci/results", "../results/")]
    [InlineData("/ci", "/ci/test results", "test%20results/")]
    public void attachments_are_linked_through_the_results_directory_relative_to_the_page(string page, string results, string expected)
    {
        if (OperatingSystem.IsWindows()) return;
        ResultsPage.AttachmentBase(page, results).ShouldBe(expected);
    }

    [Fact]
    public void a_directory_is_every_results_file_in_it_and_never_the_schema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bobcat-results-page", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Suite-abc.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "Suite-abc-2.json"), "{}");
            File.WriteAllText(Path.Combine(directory, Bobcat.Rendering.ResultsFile.SchemaFileName), "{}");

            ResultsPage.ResultsFilesIn([directory]).Select(Path.GetFileName)
                .ShouldBe(["Suite-abc-2.json", "Suite-abc.json"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void the_page_is_written_with_each_files_attachments_relative_to_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "bobcat-results-page", Guid.NewGuid().ToString("N"));
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(results);
        try
        {
            var file = Path.Combine(results, "Suite-abc.json");
            File.WriteAllText(file, """{"run":{"suite":"Suite"}}""");

            var page = ResultsPage.Write(template, Path.Combine(root, "site", "index.html"), [file]);

            page.ShouldBe(Path.Combine(root, "site", "index.html"));
            File.ReadAllText(page).ShouldContain("""{"attachmentBase":"../results/","results":{"run":{"suite":"Suite"}}}""");

            ResultsPage.Write(template, page, [file], attachmentsKept: false);
            File.ReadAllText(page).ShouldContain("\"attachmentBase\":null");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
