using Bobcat;

namespace Bobcat.Acceptance.Tests;

public class WaitForFixture : Fixture
{
    private int _outstandingCalls;
    private int _checkCalls;
    private int _drainCalls;

    // Returns 5 on the first two polls, then 0 — converges to the expected 0.
    [WaitFor(2000, PollAt = 10)]
    [Then("the outstanding count becomes {int}")]
    public int Outstanding()
    {
        _outstandingCalls++;
        return _outstandingCalls >= 3 ? 0 : 5;
    }

    // Always returns 9 — never matches the expected, so it times out.
    [WaitFor(60, PollAt = 10)]
    [Then("the never-ready count becomes {int}")]
    public int NeverReady() => 9;

    // Bool check that flips true on the second poll.
    [WaitFor(2000, PollAt = 10)]
    [Check("the system is eventually ready")]
    public bool IsReady() => ++_checkCalls >= 2;

    // Void action that throws "not ready" until the second poll.
    [WaitFor(2000, PollAt = 10)]
    [When("the queue eventually drains")]
    public void Drain()
    {
        if (++_drainCalls < 2)
            throw new InvalidOperationException("not drained yet");
    }
}
