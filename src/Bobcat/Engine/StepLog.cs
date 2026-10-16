namespace Bobcat.Engine;

/// <summary>
/// The log lines one step — or the scenario, between steps — recorded through
/// <see cref="SpecLog.Write"/> (bobcat#547), capped at <see cref="MaxLines"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Capped, and the cap is never silent.</b> Lines past the cap are counted in
/// <see cref="Suppressed"/> and every surface says "…and N more". The cap is here rather than left
/// to each caller to remember for the reason <see cref="TableReport.DefaultMaxRows"/> is:
/// <c>docs/tutorials/agent-friendly-tests.md</c> records a suite that dumped 698MB into test output
/// and turned every unreported test indeterminate.
/// </para>
/// <para>
/// <b>Locked</b>, because a step can log from a task it started and is awaiting in parallel.
/// </para>
/// </remarks>
public sealed class StepLog
{
    /// <summary>How many lines one step (or the scenario) keeps.</summary>
    public const int MaxLines = 100;

    private readonly List<string> _lines = new();
    private int _suppressed;

    /// <summary>The lines kept, in the order they were written.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines) return _lines.ToList();
        }
    }

    /// <summary>Lines written past <see cref="MaxLines"/> and not kept.</summary>
    public int Suppressed
    {
        get
        {
            lock (_lines) return _suppressed;
        }
    }

    /// <summary>True when nothing was written at all.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lines) return _lines.Count == 0 && _suppressed == 0;
        }
    }

    internal void Add(string line)
    {
        lock (_lines)
        {
            if (_lines.Count >= MaxLines) _suppressed++;
            else _lines.Add(line);
        }
    }

    /// <summary>The line every surface writes after a capped log.</summary>
    public static string MoreLine(int suppressed)
        => suppressed == 1 ? "…and 1 more line" : $"…and {suppressed} more lines";
}
