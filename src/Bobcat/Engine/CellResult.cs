namespace Bobcat.Engine;

/// <summary>
/// The result of comparing a single cell (a column value, a scalar Then, etc).
/// <para>
/// Comparison and display are deliberately unbundled: <see cref="Expected"/>,
/// <see cref="Actual"/>, and <see cref="Note"/> hold the formatted strings produced by a
/// type-aware checker, while <see cref="DisplayText"/> is a derived/legacy single-line view.
/// This structured shape is the shared spine reused by set verification, decision tables,
/// and AI-consumable JSON output.
/// </para>
/// </summary>
public class CellResult
{
    private readonly string? _displayText;

    /// <summary>
    /// Legacy constructor. Prefer the structured constructor and the
    /// <see cref="Expected"/>/<see cref="Actual"/>/<see cref="Note"/> initializers.
    /// </summary>
    public CellResult(string name, ResultStatus status, string displayText)
    {
        Name = name;
        Status = status;
        _displayText = displayText;
    }

    /// <summary>
    /// Structured constructor. Set <see cref="Expected"/>/<see cref="Actual"/>/<see cref="Note"/>
    /// via object initializer; <see cref="DisplayText"/> is then derived.
    /// </summary>
    public CellResult(string name, ResultStatus status)
    {
        Name = name;
        Status = status;
    }

    public string Name { get; }
    public ResultStatus Status { get; }

    /// <summary>
    /// Formatted expected value (a display string, never a typed object).
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>
    /// Formatted actual value (a display string, never a typed object).
    /// </summary>
    public string? Actual { get; init; }

    /// <summary>
    /// Optional supplementary note — e.g. the tolerance that was applied,
    /// or why an expected value could not be parsed.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// What this cell actually compared (issue #384). <b>Null means the producer did not say</b>,
    /// which is read as <see cref="Engine.Comparison.Equals"/> — correct for every table, set and
    /// property cell, and what keeps every existing producer unchanged.
    /// </summary>
    /// <remarks>
    /// It <b>extends</b> this type rather than replacing it, and that was the open question in
    /// #384. Three things settled it. Every existing producer is genuinely making an equality
    /// claim, so a null default is honest rather than a placeholder. <see cref="DisplayText"/>'s
    /// legacy constructor is load-bearing for the set-verification path and must not be disturbed,
    /// which rules out a new type that does not have it. And #396 established that a cell says
    /// exactly one thing with its CONTENT fields — a comparison is not a fourth kind of content
    /// but a statement about how <see cref="Expected"/> and <see cref="Actual"/> relate, so it is
    /// orthogonal to that split and sits beside it.
    /// </remarks>
    public Comparison? Comparison { get; init; }

    /// <summary>
    /// Whether this cell's shape is the equality shape — the one <c>expected 'x', got 'y'</c>
    /// states. True when nothing was said, because that is what unstated has always meant here.
    /// </summary>
    public bool IsEqualityShaped => Comparison is null or Engine.Comparison.Equals;

    /// <summary>
    /// Derived/legacy single-line description. If a literal display text was supplied
    /// through the legacy constructor it is returned verbatim; otherwise it is composed
    /// from <see cref="Expected"/>/<see cref="Actual"/>/<see cref="Note"/> and
    /// <see cref="Comparison"/>.
    /// </summary>
    public string DisplayText => _displayText ?? derive();

    private string derive()
    {
        var note = string.IsNullOrEmpty(Note) ? "" : $" ({Note})";

        return Status switch
        {
            ResultStatus.success or ResultStatus.ok => (Expected ?? Actual ?? "") + note,

            // A failed cell with no pair has nothing to compare and only its note to say — the
            // set comparer's out-of-order cell is the case (issue #396). Without this branch it
            // read "expected '', got '' (Out of order: …)", which is why that cell was still
            // being built through the legacy constructor and so never reached the wire at all.
            ResultStatus.failed when Expected is null && Actual is null => Note ?? "",

            // The comparison the cell actually made, so a non-equality assertion cannot state an
            // equality it never checked (issue #384). A unary comparison has no expected value to
            // show — "should not be null, got ''" — and everything else reads
            // "<prose> '<expected>', got '<actual>'", which for Equals is the sentence this
            // always produced.
            ResultStatus.failed when !IsEqualityShaped =>
                (Comparison!.Value.HasExpectedValue()
                    ? $"{Comparison.Value.Prose()} '{Expected}', got '{Actual}'"
                    : $"{Comparison.Value.Prose()}, got '{Actual}'") + note,

            ResultStatus.failed => $"expected '{Expected}', got '{Actual}'" + note,
            _ => Note ?? Expected ?? Actual ?? ""
        };
    }

    public Exception? Exception { get; init; }

    /// <summary>
    /// Row index for set verification results (0-based). -1 for non-table cells.
    /// </summary>
    public int RowIndex { get; init; } = -1;

    /// <summary>
    /// Return a copy of this cell with <paramref name="note"/> appended to any existing note.
    /// </summary>
    public CellResult WithNote(string note)
    {
        var combined = string.IsNullOrEmpty(Note) ? note : $"{Note}; {note}";
        return new CellResult(Name, Status)
        {
            Expected = Expected,
            Actual = Actual,
            Note = combined,
            Exception = Exception,
            RowIndex = RowIndex,

            // Carried, because without it appending a note silently turns a GreaterThan cell back
            // into an equality claim — the exact falsehood the comparison exists to prevent.
            Comparison = Comparison
        };
    }
}
