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
var executor = CommandExecutor.For(factory =>
{
    factory.RegisterCommand<ImportEventModelCommand>();
    factory.SetAppName("bobcat");
});

return await executor.ExecuteAsync(args);
