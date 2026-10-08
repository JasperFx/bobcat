using Bobcat.Console;
using JasperFx.CommandLine;

// The `bobcat` tool is a plain command host — no web server, no store, nothing that outlives the
// process.
//
// Registration is EXPLICIT rather than a scan, which is the whole point (issue #369).
// `RunJasperFxCommands(args)` handed this tool the entire JasperFx command family — `projections`,
// `event-query`, `codegen`, `describe`, `resources` and `run` — none of which can do anything
// useful without a configured application, and one of which was actively harmful: `run` is
// JasperFx's default command, so a bare `bobcat` fell through to it, started a host and blocked
// until killed. In a pipeline that hangs the job rather than failing it.
//
// BobcatRunner.Run has always registered explicitly for the same reason, in its own words:
// "nothing a consumer's assemblies carry can join this surface by accident."
ResidentInput? resident = null;

var executor = CommandExecutor.For(factory =>
{
    factory.RegisterCommand<ImportEventModelCommand>();
    factory.RegisterCommand<ResidentCommand>();

    // Running a spec project and reading its specifications: run, preview, pick, watch
    factory.RegisterCommand<Bobcat.Console.Specs.SpecRunCommand>();
    factory.RegisterCommand<Bobcat.Console.Specs.SpecPreviewCommand>();
    factory.RegisterCommand<Bobcat.Console.Specs.SpecPickCommand>();
    factory.RegisterCommand<Bobcat.Console.Specs.SpecWatchCommand>();

    // A bare `bobcat` runs the spec project found from the current directory, and so does
    // `bobcat --feature …` or `bobcat path/to/Specs`. This is NOT the #369 trap above: that `run`
    // started an application host and blocked; this one finds a spec project, runs it and exits —
    // and with no spec project to find it fails fast, which is what a pipeline needs.
    factory.DefaultCommand = typeof(Bobcat.Console.Specs.SpecRunCommand);
    factory.SetAppName("bobcat");

    // `resident` decides its own exit code, and JasperFx's true/false cannot carry it: a restart
    // is 75 (issue #397) so a parent relaunches on 75 and only on 75, and "the runner asked to be
    // replaced" must never arrive as "the command failed". Held on the input and read back after,
    // which is the arrangement BobcatRunner.Run already uses for the same reason.
    factory.ConfigureRun = run =>
    {
        if (run.Input is ResidentInput input) resident = input;
    };
});

var code = await executor.ExecuteAsync(args);

return resident?.ExitCode ?? code;
