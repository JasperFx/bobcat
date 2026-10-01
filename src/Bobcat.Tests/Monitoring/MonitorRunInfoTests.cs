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

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, _previousSession);
        Environment.SetEnvironmentVariable(MonitorRunInfo.RunTagVariable, _previousTag);
    }

    [Fact]
    public void the_agent_session_is_read_from_the_environment_it_is_already_in()
    {
        // Not a BOBCAT_* variable: Claude Code puts this in the environment of everything it
        // launches, so a run started from a session is attributable with no configuration at all.
        Environment.SetEnvironmentVariable(MonitorRunInfo.SessionVariable, "session_019U1ut5qK9");

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
}
