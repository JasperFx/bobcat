using Bobcat.Monitoring;
using Shouldly;

namespace Bobcat.Tests.Monitoring;

/// <summary>
/// What <see cref="MonitorRunInfo.Discover"/> picks up out of the environment. Serialized into its
/// own collection because it mutates process-wide state, and every variable is restored to the value
/// it had rather than blindly to null — a developer running this suite from inside an agent session
/// genuinely has CLAUDE_CODE_SESSION_ID set.
/// </summary>
[Collection("monitor-run-info")]
public class MonitorRunInfoTests : IDisposable
{
    private readonly string? _previousSession
        = Environment.GetEnvironmentVariable(MonitorRunInfo.SessionVariable);

    private readonly string? _previousTag
        = Environment.GetEnvironmentVariable(MonitorRunInfo.RunTagVariable);

    private readonly string? _previousCommand
        = Environment.GetEnvironmentVariable(MonitorRunInfo.RunCommandVariable);

    private readonly string? _previousCommandFile
        = Environment.GetEnvironmentVariable(MonitorRunInfo.RunCommandFileVariable);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "bobcat-run-info", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, _previousSession);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, _previousTag);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, _previousCommand);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandFileVariable, _previousCommandFile);

        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory is not worth failing a test over */ }
    }

    /// <summary>A command file holding <paramref name="contents"/>, pointed at by the variable.</summary>
    private string commandFile(string? contents)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "current.command");

        if (contents is not null) File.WriteAllText(path, contents);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandFileVariable, path);

        return path;
    }

    // --- BOBCAT_RUN_COMMAND_FILE (issue #402): the per-request command id.

    [Fact]
    public void the_command_file_is_read_as_this_request_s_command()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);
        commandFile("c-from-the-file");

        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-from-the-file");
    }

    [Fact]
    public void the_file_wins_over_the_variable_because_it_is_the_narrower_claim()
    {
        // This is the whole reason the file exists. A warm child inherits BOBCAT_RUN_COMMAND once,
        // at launch, and keeps it for life — so a process serving several commands would report
        // every one of them as the first, which is exactly the mis-attribution issue #401 fixed.
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "c-from-launch");
        commandFile("c-this-request");

        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-this-request");
    }

    [Fact]
    public void the_file_is_re_read_per_discover_so_a_warm_process_sees_each_command()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);
        var path = commandFile("c-one");

        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-one");

        // The parent rewrites it before the next request. Nothing is cached: Discover runs per
        // run bracket, which is per request now (#402), so the new value is simply read.
        File.WriteAllText(path, "c-two");

        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-two");
    }

    [Fact]
    public void trailing_whitespace_in_the_file_is_not_part_of_the_command()
    {
        // A parent writing with a shell redirect gets a newline for free, and a command id with a
        // trailing newline joins against nothing on the console's side.
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);
        commandFile("c-one\n");

        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-one");
    }

    [Fact]
    public void a_missing_or_empty_file_falls_back_to_the_variable()
    {
        // A cold child has no per-request file, and an empty one is a parent that has not written
        // yet. Neither is an error: run attribution must never be able to fail a run.
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "c-from-launch");

        Environment.SetEnvironmentVariable(
            MonitorRunInfo.RunCommandFileVariable, Path.Combine(_directory, "not-there.command"));
        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-from-launch");

        commandFile("   ");
        MonitorRunInfo.Discover("xunit").Command.ShouldBe("c-from-launch");
    }

    [Fact]
    public void a_command_from_the_file_suppresses_the_agent_session_like_any_other()
    {
        // Issue #401's rule is keyed on Command being set, not on how it was discovered — so it
        // rides along for free, which is the point of having written it as a getter.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_019U1ut5qK9");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);
        commandFile("c-this-request");

        var info = MonitorRunInfo.Discover("xunit");

        info.Command.ShouldBe("c-this-request");
        info.Session.ShouldBeNull();
    }

    [Fact]
    public void the_agent_session_is_read_from_the_environment_it_is_already_in()
    {
        // Not a BOBCAT_* variable: Claude Code puts this in the environment of everything it
        // launches, so a run started from a session is attributable with no configuration at all.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_019U1ut5qK9");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandFileVariable, null);

        MonitorRunInfo.Discover("in-process").Session.ShouldBe("session_019U1ut5qK9");
    }

    [Fact]
    public void no_session_variable_is_null_rather_than_empty()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, null);

        MonitorRunInfo.Discover("in-process").Session.ShouldBeNull();
    }

    [Fact]
    public void an_empty_session_variable_is_also_null()
    {
        // Same rule the tag follows. An empty string on the wire would read as "there was a
        // session" to anything checking for null.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "");

        MonitorRunInfo.Discover("in-process").Session.ShouldBeNull();
    }

    [Fact]
    public void the_session_and_the_tag_are_independent()
    {
        // They answer different questions — which agent ran this, and what work it speaks for —
        // and a run routinely has one without the other.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_abc");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, null);

        var info = MonitorRunInfo.Discover("in-process");

        info.Session.ShouldBe("session_abc");
        info.Tag.ShouldBeNull();
    }

    [Fact]
    public void the_command_that_asked_for_the_run_is_read_from_the_environment()
    {
        // Issue #392. A BOBCAT_* variable, unlike the session: Bobcat is the thing asking for this
        // one. A cold command launches a child test host, and the id travels down to it exactly as
        // BOBCAT_RUN_ID does.
        Environment.SetEnvironmentVariable(
            MonitorRunInfo.RunCommandVariable, "0f2b6c5e-9b6f-4f0a-9f42-6d4d7f1a02c7");

        MonitorRunInfo.Discover("in-process").Command
            .ShouldBe("0f2b6c5e-9b6f-4f0a-9f42-6d4d7f1a02c7");
    }

    [Fact]
    public void no_command_variable_is_null_rather_than_empty()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);

        MonitorRunInfo.Discover("in-process").Command.ShouldBeNull();
    }

    [Fact]
    public void an_empty_command_variable_is_also_null()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "");

        MonitorRunInfo.Discover("in-process").Command.ShouldBeNull();
    }

    [Fact]
    public void the_command_and_the_tag_are_independent_strings()
    {
        // The reason Command is not a reuse of Tag: a slice's specs re-run from a console carry
        // the command that asked AND the plan node they are still attributed to.
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "cmd-7");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, "wave-2/daemon");

        var info = MonitorRunInfo.Discover("in-process");

        info.Command.ShouldBe("cmd-7");
        info.Tag.ShouldBe("wave-2/daemon");
    }

    // --- Issue #401: a commanded run is not the launching session's run.

    [Fact]
    public void a_commanded_run_carries_no_session_even_when_the_variable_is_set()
    {
        // Found live driving a 0.29.0 resident runner from Stoat. A resident runner started from
        // an agent's terminal inherits that agent's session id and holds it for its whole life, so
        // every run it made for a monitor command was stamped with it — and the agent page said
        // "started by this session" about runs a person had pressed in the UI.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_that_launched_me");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "cmd-7");

        var info = MonitorRunInfo.Discover("resident");

        info.Command.ShouldBe("cmd-7", "which is the true answer to who asked");
        info.Session.ShouldBeNull();
    }

    [Fact]
    public void the_tag_survives_a_command_even_though_the_session_does_not()
    {
        // Only the session is suppressed, and only because the command answers the same question
        // better. The tag answers a different one — what work the run speaks for — so a commanded
        // re-run of a slice's specs is still attributed to that slice's plan node.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_abc");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, "cmd-7");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, "wave-2/daemon");

        var info = MonitorRunInfo.Discover("resident");

        info.Tag.ShouldBe("wave-2/daemon");
        info.Session.ShouldBeNull();
    }

    [Fact]
    public void a_command_set_after_the_fact_suppresses_the_session_too()
    {
        // The rule is a getter consulting Command rather than something a caller clears, because
        // the resident runner does not use the variable at all: it sets BobcatRunner.MonitorCommand
        // and the info is rebuilt with `with { Command = … }`. A rule that only ran inside
        // Discover would miss exactly the case the issue was filed for.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_abc");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);

        var discovered = MonitorRunInfo.Discover("resident");
        discovered.Session.ShouldBe("session_abc", "nothing commanded this one");

        (discovered with { Command = "cmd-9" }).Session.ShouldBeNull();
    }
}
