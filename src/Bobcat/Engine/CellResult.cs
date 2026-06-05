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
    /// Derived/legacy single-line description. If a literal display text was supplied
    /// through the legacy constructor it is returned verbatim; otherwise it is composed
    /// from <see cref="Expected"/>/<see cref="Actual"/>/<see cref="Note"/>.
    /// </summary>
    public string DisplayText => _displayText ?? Derive();

    private string Derive()
    {
        var note = string.IsNullOrEmpty(Note) ? "" : $" ({Note})";

        return Status switch
        {
            ResultStatus.success or ResultStatus.ok => (Expected ?? Actual ?? "") + note,
            ResultStatus.failed => $"expected '{Expected}', got '{Actual}'" + note,
            _ => Note ?? Expected ?? Actual ?? ""
        };
    }

    public Exception? Exception { get; init; }

    /// <summary>
    /// Row index for set verification results (0-based). -1 for non-table cells.
    /// </summary>
    public int RowIndex { get; init; } = -1;
}
