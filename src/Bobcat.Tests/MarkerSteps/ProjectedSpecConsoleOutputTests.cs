using System.Text;
using Shouldly;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// Where the exit-time report goes when standard output is captured (bobcat#676): with stdout
/// redirected, the specifications were lost while a terminal and <c>BOBCAT_SPEC_CONSOLE_FILE</c>
/// both printed them. The report now goes through a stream it opens itself, so a writer the process
/// swapped in and closed before exit cannot swallow it.
/// </summary>
public class ProjectedSpecConsoleOutputTests
{
    [Fact]
    public void a_redirected_report_does_not_depend_on_the_writer_console_out_holds_at_exit()
    {
        var originalOut = Console.Out;
        var originalRedirected = ProjectedSpecConsole.OutputRedirected;
        var originalOpen = ProjectedSpecConsole.OpenStandardOutput;
        var standardOutput = new MemoryStream();

        // A writer some test or test platform installed, and closed, before the process exited.
        var closed = new StringWriter();
        closed.Dispose();

        try
        {
            ProjectedSpecConsole.OutputRedirected = () => true;
            ProjectedSpecConsole.OpenStandardOutput = () => standardOutput;
            ProjectedSpecConsole.Enable();

            using (ScenarioRecorder.Begin(
                       "Captured output", "a report written into a captured stream", publisher: null,
                       runId: Guid.NewGuid()))
            {
                using (ScenarioRecorder.Step("Then", "the report reaches standard output"))
                {
                }
            }

            Console.SetOut(closed);
            ProjectedSpecConsole.Render();
        }
        finally
        {
            Console.SetOut(originalOut);
            ProjectedSpecConsole.Disable();
            ProjectedSpecConsole.OutputRedirected = originalRedirected;
            ProjectedSpecConsole.OpenStandardOutput = originalOpen;
        }

        var text = Encoding.UTF8.GetString(standardOutput.ToArray());
        text.ShouldContain("Feature: Captured output");
        text.ShouldContain("a report written into a captured stream");
        text.ShouldContain("specification(s)");

        // A captured stream is laid out as it always was: plain text, no escape codes.
        text.ShouldNotContain("\u001b[");
    }
}
