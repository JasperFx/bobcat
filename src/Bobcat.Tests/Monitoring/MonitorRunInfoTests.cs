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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, _previousSession);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, _previousTag);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, _previousCommand);
    }

    [Fact]
    public void the_agent_session_is_read_from_the_environment_it_is_already_in()
    {
        // Not a BOBCAT_* variable: Claude Code puts this in the environment of everything it
        // launches, so a run started from a session is attributable with no configuration at all.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_019U1ut5qK9");
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunCommandVariable, null);

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
