using Bobcat.Engine;

namespace Bobcat.Rendering;

/// <summary>
/// Intermediate rendering model for a single scenario's results.
/// Feeds both Spectre.Console and later HTML rendering.
/// </summary>
public class SpecRender
{
    public string Title { get; init; } = "";
    public string? FeatureTitle { get; init; }
    public bool Succeeded { get; init; }
    public List<StepRender> Steps { get; init; } = new();
    public Counts Counts { get; init; } = new();
    public long DurationMs { get; init; }

    public static SpecRender FromResults(string title, ExecutionResults results, string? featureTitle = null)
    {
        var steps = results.Steps.Select(StepRender.FromStepResult).ToList();
        var durationMs = results.Steps.Where(s => s.End > 0).Select(s => s.End).DefaultIfEmpty(0).Max();

        return new SpecRender
        {
            Title = title,
            FeatureTitle = featureTitle,
            Succeeded = results.Counts.Succeeded,
            Steps = steps,
            Counts = results.Counts,
            DurationMs = durationMs
        };
    }
}

/// <summary>
/// Rendering model for a single step.
/// </summary>
public class StepRender
{
    public string StepId { get; init; } = "";
    public StepKind Kind { get; init; }
    public string StepText { get; init; } = "";
    public ResultStatus Status { get; init; }
    public FailureLevel FailureLevel { get; init; }
    public long DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ExceptionType { get; init; }
    public SetVerificationRender? SetVerification { get; init; }
    public List<CellRender> Cells { get; init; } = new();

    public static StepRender FromStepResult(StepResult result)
    {
        SetVerificationRender? sv = null;
        List<CellRender> cells = new();

        if (result.IsSetVerification && result.SetVerificationColumns != null)
        {
            sv = SetVerificationRender.FromStepResult(result);
        }
        else
        {
            cells = result.Cells.Select(c => new CellRender
            {
                Name = c.Name,
                Status = c.Status,
                DisplayText = c.DisplayText
            }).ToList();
        }

        return new StepRender
        {
            StepId = result.StepId,
            Kind = result.StepKind,
            StepText = result.StepText ?? result.StepId,
            Status = result.StepStatus,
            FailureLevel = result.FailureLevel,
            DurationMs = result.End > result.Start ? result.End - result.Start : 0,
            ErrorMessage = result.Exception?.Message,
            ExceptionType = result.Exception?.GetType().Name,
            SetVerification = sv,
            Cells = cells
        };
    }
}

/// <summary>
/// Rendering model for a cell (non-table result).
/// </summary>
public class CellRender
{
    public string Name { get; init; } = "";
    public ResultStatus Status { get; init; }
    public string DisplayText { get; init; } = "";
}

/// <summary>
/// Rendering model for set verification table results.
/// </summary>
public class SetVerificationRender
{
    public List<string> Columns { get; init; } = new();
    public List<SetVerificationRowRender> Rows { get; init; } = new();

    public static SetVerificationRender FromStepResult(StepResult result)
    {
        var columns = result.SetVerificationColumns?.ToList() ?? new();
        var rows = new List<SetVerificationRowRender>();

        foreach (var group in result.Cells.GroupBy(c => c.RowIndex).OrderBy(g => g.Key))
        {
            var cells = group.ToList();
            var missingCell = cells.FirstOrDefault(c => c.Name == "missing-row");
            var extraCell = cells.FirstOrDefault(c => c.Name == "extra-row");

            if (missingCell != null)
            {
                rows.Add(new SetVerificationRowRender
                {
                    RowType = SetVerificationRowType.Missing,
                    Description = missingCell.DisplayText
                });
            }
            else if (extraCell != null)
            {
                rows.Add(new SetVerificationRowRender
                {
                    RowType = SetVerificationRowType.Extra,
                    Description = extraCell.DisplayText
                });
            }
            else
            {
                var row = new SetVerificationRowRender { RowType = SetVerificationRowType.Matched };
                foreach (var col in columns)
                {
                    var cell = cells.FirstOrDefault(c => c.Name == col);
                    row.Cells.Add(new SetVerificationCellRender
                    {
                        Column = col,
                        Status = cell?.Status ?? ResultStatus.ok,
                        DisplayText = cell?.DisplayText ?? ""
                    });
                }
                rows.Add(row);
            }
        }

        return new SetVerificationRender { Columns = columns, Rows = rows };
    }
}

public class SetVerificationRowRender
{
    public SetVerificationRowType RowType { get; init; }
    public List<SetVerificationCellRender> Cells { get; init; } = new();
    public string? Description { get; init; }
    public bool AllCellsOk => Cells.All(c => c.Status == ResultStatus.success);
}

public class SetVerificationCellRender
{
    public string Column { get; init; } = "";
    public ResultStatus Status { get; init; }
    public string DisplayText { get; init; } = "";
}

public enum SetVerificationRowType
{
    Matched,
    Missing,
    Extra
}
