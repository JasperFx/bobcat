using Bobcat.Console;

// The `bobcat` tool is a command host. One command, `ui` (bobcat#524), serves a web console until
// Ctrl+C; nothing else starts a server. The commands are registered in `BobcatCommands`, explicitly
// and never by a scan (issue #369); a usage mistake is one line and exit 1 (bobcat#785).
ResidentInput? resident = null;

var executor = BobcatCommands.Executor(run =>
{
    // `resident` decides its own exit code, and JasperFx's true/false cannot carry it: a restart
    // is 75 (issue #397) so a parent relaunches on 75 and only on 75, and "the runner asked to be
    // replaced" must never arrive as "the command failed". Held on the input and read back after.
    if (run.Input is ResidentInput input) resident = input;
});

var code = await executor.ExecuteAsync(args);

return resident?.ExitCode ?? code;
