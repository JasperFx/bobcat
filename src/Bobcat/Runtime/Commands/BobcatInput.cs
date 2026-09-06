using JasperFx.CommandLine;

namespace Bobcat.Runtime.Commands;

/// <summary>
/// Shared input for the Bobcat command family (issue #206). The filter flags mirror what
/// <see cref="BobcatRunner.RunAll"/> takes, so <c>run</c>, <c>list</c>, <c>preview</c> and
/// <c>interactive</c> all narrow the same way.
/// </summary>
public class BobcatInput
{
    /// <summary>
    /// The configured runner, injected by <see cref="BobcatRunner.Run"/> before the command
    /// executes — never bound from the command line.
    /// </summary>
    [IgnoreOnCommandLine]
    public BobcatRunner Runner { get; set; } = null!;

    /// <summary>
    /// The exit code the command decided on — Bobcat's contract (0 pass, 1 regression fail,
    /// 2 catastrophic), which <see cref="BobcatRunner.Run"/> returns verbatim instead of
    /// JasperFx's own success/failure mapping. Null means the command never got far enough
    /// to decide, and JasperFx's code stands.
    /// </summary>
    [IgnoreOnCommandLine]
    public int? ExitCode { get; set; }

    [Description("Only features whose title contains this text (case-insensitive)")]
    public string? FeatureFlag { get; set; }

    [Description("Only scenarios carrying this tag")]
    public string? TagFlag { get; set; }
}

/// <summary>Input for the <c>run</c> command.</summary>
public class RunInput : BobcatInput
{
    [Description("Emit the machine-readable JSON report instead of the console rendering")]
    public bool JsonFlag { get; set; }
}
