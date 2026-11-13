using System.Text;
using Bobcat.Engine;

namespace Bobcat.Rendering;

/// <summary>
/// How <see cref="MarkdownSpecText"/> renders one scenario: where its heading sits, where its
/// attachments are from the page, and which attempt of a retried specification it is.
/// </summary>
public sealed record MarkdownSpecOptions
{
    /// <summary>The heading level of the scenario's title; its sections sit one level below.</summary>
    public int HeadingLevel { get; init; } = 1;

    /// <summary>Write the title heading. Off when the caller writes its own heading above the scenario.</summary>
    public bool IncludeHeading { get; init; } = true;

    /// <summary>
    /// The results directory as the page sees it — a relative URL ending in <c>/</c>, or empty when
    /// the page sits in it. Null when the directory was not kept: attachments are then named and
    /// linked to nothing, the HTML results page's rule.
    /// </summary>
    public string? AttachmentBase { get; init; }

    /// <summary>A link for the feature's name on the feature line; the name alone when null.</summary>
    public string? FeatureLink { get; init; }

    /// <summary>This attempt's number, 1-based, when the specification ran more than once in the run.</summary>
    public int? Attempt { get; init; }

    /// <summary>How many attempts the run made at the specification.</summary>
    public int? Attempts { get; init; }

    public static MarkdownSpecOptions Default { get; } = new();
}

/// <summary>
/// One scenario as GitHub-flavoured Markdown (bobcat#553): the rendering a documentation page
/// embeds — a VitePress <c>@include</c>, a Docusaurus import, or a committed file GitHub renders.
/// Storyteller's <c>dotnet stdocs</c>, over the results file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same model, another output.</b> This renders <see cref="SpecRender"/>, the model the console,
/// the JSON report and <see cref="PlainSpecText"/> render — read back from a results file by
/// <see cref="ResultsFileReader"/> when the scenario comes from one — so the four cannot disagree
/// about what a cell or a verdict said. The words are the ones every other surface uses:
/// <see cref="SpecRender.Verdict"/>, <see cref="CellRender.DisplayText"/> (<c>expected 'x', got
/// 'y'</c>, or the value when it agreed), <see cref="CommandLineRenderer.ShowsStatusColumn"/> for
/// whether a grid earns a verdict column, <see cref="StepLog.MoreLine"/> for a capped log.
/// </para>
/// <para>
/// <b>Deterministic.</b> Nothing here reads a clock or a path, so the same scenario renders to the
/// same bytes, and a documentation build that regenerates its specifications sees no change when
/// nothing changed.
/// </para>
/// <para>
/// <b>Rendering can never fail a run.</b> The entry points return nothing rather than throwing, the
/// invariant <see cref="PlainSpecText"/> keeps; a caller that gets no lines says so.
/// </para>
/// </remarks>
public static class MarkdownSpecText
{
    private const string VerdictMissing = "MISSING";
    private const string VerdictExtra = "EXTRA";
    private const string VerdictError = "ERROR";
    private const string VerdictOrder = "ORDER";
    private const string VerdictOk = "OK";
    private const string VerdictFail = "FAIL";

    /// <summary>The scenario as Markdown lines. Empty when the rendering throws.</summary>
    public static IReadOnlyList<string> Lines(SpecRender spec, MarkdownSpecOptions? options = null)
    {
        options ??= MarkdownSpecOptions.Default;

        try
        {
            var writer = new Writer(options);
            writer.Scenario(spec);
            return writer.Lines;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The scenario as one Markdown block, with <c>\n</c> line endings, or an empty string.</summary>
    public static string Render(SpecRender spec, MarkdownSpecOptions? options = null)
    {
        var lines = Lines(spec, options);
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    /// <summary>The glyph every surface puts beside a verdict: ✓ passed, ✗ failed, ◌ not yet passing.</summary>
    public static string Glyph(SpecRender spec)
        => spec.AcceptanceFailing ? "◌" : spec.Succeeded ? "✓" : "✗";

    /// <summary>
    /// The verdict with the words the console adds to it: <c>passed</c>; <c>failed</c>;
    /// <c>acceptance: failing — not yet passing, does not fail the build</c>; <c>acceptance: passing
    /// — ready to promote to Regression</c>.
    /// </summary>
    public static string VerdictSentence(SpecRender spec)
        => spec.AcceptanceFailing
            ? $"{spec.Verdict} — not yet passing, does not fail the build"
            : spec.ReadyToPromote
                ? $"{spec.Verdict} — {SpecLifecycle.ReadyToPromote}"
                : spec.Verdict;

    /// <summary>A duration as a reader wants it: <c>39ms</c>, <c>1.2s</c>.</summary>
    public static string Duration(long ms)
        => ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.0}s";

    /// <summary>
    /// Text made literal for Markdown: every character that could open a span, a link, a tag or a
    /// table cell is backslash-escaped, which CommonMark, GitHub and VitePress all read as the
    /// character itself.
    /// </summary>
    public static string Escape(string text)
    {
        if (text.Length == 0) return text;

        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|' or '~') builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Text for one table cell: escaped, with a line break inside it as <c>&lt;br&gt;</c>.</summary>
    public static string Cell(string text)
        => Escape(text.Replace("\r\n", "\n")).Replace("\n", "<br>");

    private sealed class Writer
    {
        private readonly MarkdownSpecOptions _options;
        private readonly List<string> _lines = new();

        public Writer(MarkdownSpecOptions options)
        {
            _options = options;
        }

        public IReadOnlyList<string> Lines => _lines;

        public void Scenario(SpecRender spec)
        {
            if (_options.IncludeHeading)
            {
                _lines.Add($"{new string('#', Math.Clamp(_options.HeadingLevel, 1, 6))} {Escape(spec.Title)}");
                _lines.Add("");
            }

            if (spec.FeatureTitle is { Length: > 0 } feature)
            {
                var name = _options.FeatureLink is { Length: > 0 } link ? $"[{Escape(feature)}]({link})" : Escape(feature);
                _lines.Add($"**Feature:** {name}");
                _lines.Add("");
            }

            _lines.Add(verdictLine(spec));
            _lines.Add("");

            if (spec.Steps.Count == 0)
            {
                // The console's pending line: a specification that declares nothing has not passed anything.
                _lines.Add("○ this specification declares no steps");
                _lines.Add("");
            }
            else
            {
                foreach (var step in spec.Steps) this.step(step);
                _lines.Add("");
            }

            exceptions(spec);
            scenarioFailure(spec);
            logs(spec);
            attachments(spec.Attachments, spec.SuppressedAttachments, indent: "");
            reports(spec);

            trimTrailingBlank();
        }

        private string verdictLine(SpecRender spec)
        {
            var parts = new List<string> { $"**{Glyph(spec)} {VerdictSentence(spec)}**" };

            if (spec.Steps.Count == 0 && spec.Succeeded) parts.Add("pending");

            parts.Add($"Rights: {spec.Counts.Rights}, Wrongs: {spec.Counts.Wrongs}, Errors: {spec.Counts.Errors}");

            if (spec.DurationMs > 0)
            {
                var steps = spec.StepsMs is { } stepsMs ? $" (steps {Duration(stepsMs)})" : "";
                parts.Add(Duration(spec.DurationMs) + steps);
            }

            if (_options is { Attempt: { } attempt, Attempts: { } attempts } && attempts > 1)
            {
                parts.Add($"attempt {attempt} of {attempts}");
            }

            return string.Join(" · ", parts);
        }

        private void step(StepRender step)
        {
            var indent = new string(' ', step.Depth * 2);
            var inner = indent + "  ";

            if (CommandLineRenderer.IsNote(step.Keyword))
            {
                _lines.Add($"{indent}- *» {Escape(step.StepText)}*");
                return;
            }

            var keyword = step.Keyword switch
            {
                { Length: > 0 } k => $"**{Escape(k)}** ",
                "" => "",
                _ => step.Kind switch
                {
                    StepKind.Given => "**Given** ",
                    StepKind.When => "**When** ",
                    StepKind.Then => "**Then** ",
                    StepKind.SetUp => "**Setup** ",
                    StepKind.TearDown => "**Teardown** ",
                    _ => ""
                }
            };

            var sentence = Escape(step.StepText);

            if (step.NotRun)
            {
                _lines.Add($"{indent}- ○ {keyword}{sentence} — *not run*");
                return;
            }

            if (step.IsNarrative && step.Status == ResultStatus.ok)
            {
                _lines.Add($"{indent}- · {keyword}{sentence} — *declared*");
                return;
            }

            var glyph = step.Status switch
            {
                ResultStatus.success => "✓",
                ResultStatus.failed => "✗",
                ResultStatus.error => "!",
                ResultStatus.ok => "○",
                _ => "?"
            };

            var duration = step.DurationMs > 0 ? $" *({Duration(step.DurationMs)})*" : "";
            _lines.Add($"{indent}- {glyph} {keyword}{sentence}{duration}");

            if (step.Status == ResultStatus.failed && step.ErrorMessage is { Length: > 0 } wrong)
            {
                // A wrong says what it is, on the line, with no stack.
                bullet(inner, "✗ " + Escape(wrong));
            }
            else if (step.Status == ResultStatus.error && step.ErrorMessage is not null)
            {
                _lines.Add($"{inner}- ! {Escape(step.ExceptionType ?? "exception")} — see below");
            }

            if (step.SetVerification is { Columns.Count: > 0 } grid)
            {
                _lines.Add("");
                table(grid, inner);
                _lines.Add("");
            }

            var inline = CommandLineRenderer.CellsRenderedInline(step);
            foreach (var cell in step.Cells)
            {
                if (step.SetVerification is not null && SetVerificationRender.BelongsToARow(cell.RowIndex, cell.Name)) continue;
                if (inline.Contains(cell.Name)) continue;

                var cellGlyph = cell.Status switch
                {
                    ResultStatus.success => "✓",
                    ResultStatus.failed => "✗",
                    ResultStatus.error => "!",
                    _ => "·"
                };
                bullet(inner, $"{cellGlyph} {Escape(cell.Name)}: {Escape(cell.DisplayText)}");
            }

            attachments(step.Attachments, 0, inner);

            if (step.Logs.Count > 0 || step.SuppressedLogs > 0)
            {
                _lines.Add($"{inner}- Logs:");
                _lines.Add("");
                fenced(step.Logs, step.SuppressedLogs, inner + "  ");
                _lines.Add("");
            }

            foreach (var (key, value) in step.Diagnostics)
            {
                bullet(inner, $"{Escape(key)}: {Escape(value)}");
            }
        }

        /// <summary>A nested bullet whose text may span lines; the continuation lines stay in the item.</summary>
        private void bullet(string indent, string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            _lines.Add($"{indent}- {lines[0].TrimEnd()}");
            foreach (var line in lines.Skip(1)) _lines.Add($"{indent}  {line.TrimEnd()}");
        }

        private void table(SetVerificationRender grid, string indent)
        {
            var judged = CommandLineRenderer.ShowsStatusColumn(grid);

            var header = new List<string> { "#" };
            header.AddRange(grid.Columns.Select(Cell));
            if (judged) header.Add(Cell(CommandLineRenderer.VerdictColumnHeader(grid)));

            _lines.Add($"{indent}| {string.Join(" | ", header)} |");
            _lines.Add($"{indent}|{string.Concat(header.Select(_ => "---|"))}");

            var number = 0;
            foreach (var row in grid.Rows)
            {
                number++;
                var values = new List<string> { number.ToString() };

                switch (row.RowType)
                {
                    case SetVerificationRowType.Missing:
                        values.AddRange(absent(grid, row, "-"));
                        if (judged) values.Add(VerdictMissing);
                        break;
                    case SetVerificationRowType.Extra:
                        values.AddRange(absent(grid, row, "..."));
                        if (judged) values.Add(VerdictExtra);
                        break;
                    default:
                        foreach (var cell in row.Cells)
                        {
                            values.Add(cell.Status == ResultStatus.error && cell.DisplayText.Length == 0 ? "!" : Cell(cell.DisplayText));
                        }

                        if (judged)
                        {
                            values.Add(row.RowType switch
                            {
                                SetVerificationRowType.Errored => VerdictError,
                                SetVerificationRowType.OutOfOrder => VerdictOrder,
                                _ => row.AllCellsOk ? VerdictOk : VerdictFail
                            });
                        }

                        break;
                }

                _lines.Add($"{indent}| {string.Join(" | ", values)} |");
            }

            // A row's own reason, under the grid rather than squeezed into a cell.
            number = 0;
            var described = false;
            foreach (var row in grid.Rows)
            {
                number++;
                if (row.RowType is not (SetVerificationRowType.Errored or SetVerificationRowType.OutOfOrder)) continue;
                if (string.IsNullOrEmpty(row.Description)) continue;

                if (!described)
                {
                    _lines.Add("");
                    described = true;
                }

                bullet(indent, $"row {number}: {Escape(row.Description)}");
            }
        }

        /// <summary>An absent row's values per column — the console's dashes or dots when it carried none.</summary>
        private static IEnumerable<string> absent(SetVerificationRender grid, SetVerificationRowRender row, string none)
        {
            if (row.Cells.Count == 0) return grid.Columns.Select(_ => none);
            return grid.Columns.Select(column => Cell(row.Cells.FirstOrDefault(c => c.Column == column)?.DisplayText ?? none));
        }

        private void exceptions(SpecRender spec)
        {
            // A real throw, whether the exception is in hand (in process) or only its name and
            // message are (read back from a results file, which carries no stack).
            var errored = spec.Steps
                .Where(x => x.Exception is not null || (x.Status == ResultStatus.error && x.ExceptionType is not null))
                .ToList();
            if (errored.Count == 0) return;

            foreach (var step in errored)
            {
                _lines.Add($"**{Escape(step.StepText)}**");
                _lines.Add("");

                var block = new List<string>();
                if (step.Exception is { } exception)
                {
                    block.Add($"{exception.GetType().Name}: {exception.Message}");
                    var filtered = SpecStackTrace.Filter(exception);
                    block.AddRange(filtered.Frames.Select(f => "  " + SpecStackTrace.Shorten(f)));
                    if (filtered.Hidden > 0) block.Add($"  ({filtered.Hidden} framework frames hidden)");

                    for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
                    {
                        block.Add($"--- {inner.GetType().Name}: {inner.Message}");
                        block.AddRange(SpecStackTrace.Filter(inner).Frames.Select(f => "  " + SpecStackTrace.Shorten(f)));
                    }
                }
                else
                {
                    block.Add(step.ErrorMessage is { Length: > 0 } message ? $"{step.ExceptionType}: {message}" : step.ExceptionType!);
                }

                fenced(block, 0, "");
                _lines.Add("");
            }
        }

        private void scenarioFailure(SpecRender spec)
        {
            if (spec.ScenarioFailure is not { } failure) return;

            if (spec.ScenarioFailureCells.Count > 0)
            {
                foreach (var cell in spec.ScenarioFailureCells)
                {
                    bullet("", $"✗ {Escape(cell.Name)}: {Escape(cell.DisplayText)}");
                }
            }
            else
            {
                fenced(failure.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).ToList(), 0, "");
            }

            _lines.Add("");
        }

        private void logs(SpecRender spec)
        {
            if (spec.Logs.Count == 0 && spec.SuppressedLogs == 0) return;

            _lines.Add("**Logs**");
            _lines.Add("");
            fenced(spec.Logs, spec.SuppressedLogs, "");
            _lines.Add("");
        }

        private void attachments(IReadOnlyList<AttachmentRender> attachments, int suppressed, string indent)
        {
            if (attachments.Count == 0 && suppressed == 0) return;

            foreach (var attachment in attachments)
            {
                var size = $"{Escape(attachment.MediaType)}, {SpecAttachment.Describe(attachment.Size)}";
                var href = attachment.Omitted is null && attachment.Path is { Length: > 0 } path && _options.AttachmentBase is { } @base
                    ? @base + string.Join('/', path.Split('/').Select(Uri.EscapeDataString))
                    : null;

                if (href is null)
                {
                    var why = attachment.Omitted ?? "not kept with these results";
                    _lines.Add($"{indent}- 📎 {Escape(attachment.Name)} ({size}) — {Escape(why)}");
                    continue;
                }

                _lines.Add($"{indent}- 📎 [{Escape(attachment.Name)}]({href}) ({size})");
                if (attachment.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    _lines.Add("");
                    _lines.Add($"{indent}  ![{Escape(attachment.Name)}]({href})");
                    _lines.Add("");
                }
            }

            if (suppressed > 0)
            {
                _lines.Add($"{indent}- …and {suppressed} more {(suppressed == 1 ? "attachment" : "attachments")} not kept");
            }

            if (indent.Length == 0) _lines.Add("");
        }

        private void reports(SpecRender spec)
        {
            foreach (var report in spec.Reports)
            {
                _lines.Add($"**{Escape(report.Title)}**");
                _lines.Add("");

                if (report.Grid.Columns.Count > 0)
                {
                    table(report.Grid, "");
                }

                if (report.SuppressedRows > 0)
                {
                    _lines.Add("");
                    _lines.Add($"…and {report.SuppressedRows} more {(report.SuppressedRows == 1 ? "row" : "rows")} not shown");
                }

                _lines.Add("");
            }
        }

        /// <summary>
        /// Lines in a code fence, with the capped-log line after them. The fence is longer than any
        /// run of backticks inside, so a log line cannot close it.
        /// </summary>
        private void fenced(IReadOnlyList<string> lines, int suppressed, string indent)
        {
            var longest = lines
                .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, "`+").Select(m => m.Length))
                .DefaultIfEmpty(0)
                .Max();
            var fence = new string('`', Math.Max(3, longest + 1));

            _lines.Add(indent + fence);
            foreach (var line in lines)
            {
                foreach (var part in line.Replace("\r\n", "\n").Split('\n')) _lines.Add(indent + part.TrimEnd());
            }

            if (suppressed > 0) _lines.Add(indent + StepLog.MoreLine(suppressed));
            _lines.Add(indent + fence);
        }

        private void trimTrailingBlank()
        {
            while (_lines.Count > 0 && _lines[^1].Length == 0) _lines.RemoveAt(_lines.Count - 1);
        }
    }
}
