using JasperFx.CommandLine;
using JasperFx.CommandLine.Help;
using JasperFx.CommandLine.Parsing;

namespace Bobcat.Console;

/// <summary>
/// The <c>bobcat</c> tool's commands, registered explicitly rather than by a scan (issue #369), and
/// the executor that runs them.
/// </summary>
/// <remarks>
/// <c>RunJasperFxCommands(args)</c> would hand this tool the entire JasperFx command family —
/// <c>projections</c>, <c>event-query</c>, <c>codegen</c>, <c>describe</c>, <c>resources</c> and
/// <c>run</c> — none of which can do anything useful without a configured application, and one of
/// which was actively harmful: <c>run</c> is JasperFx's default command, so a bare <c>bobcat</c> fell
/// through to it, started a host and blocked until killed. In a pipeline that hangs the job rather
/// than failing it. Registering explicitly means nothing a consumer's assemblies carry can join this
/// surface by accident. The list is here, in one place, so a test can read exactly what the tool
/// registers (<c>Bobcat.Console.Tests</c> checks every command's flags against it).
/// </remarks>
public static class BobcatCommands
{
    /// <summary>The name a person types; also what a usage error points at (<c>bobcat help run</c>).</summary>
    public const string AppName = "bobcat";

    /// <summary>Register every command the tool offers.</summary>
    public static void Register(CommandFactory factory)
    {
        factory.RegisterCommand<ImportEventModelCommand>();
        factory.RegisterCommand<ResidentCommand>();

        // The console (bobcat#524): the run board and the Event Model canvas in the browser, and the
        // design-time loop that keeps the canvas live as an application is edited.
        factory.RegisterCommand<UiCommand>();
        factory.RegisterCommand<Bobcat.Ui.EventModel.WatchEventModelCommand>();
        factory.RegisterCommand<UiContractsCommand>();

        // The results file's JSON Schema, generated rather than committed (bobcat#604).
        factory.RegisterCommand<ResultsSchemaCommand>();

        // The HTML results page for results files already written (bobcat#541); `run --html` writes
        // the same page for the run it does.
        factory.RegisterCommand<ResultsHtmlCommand>();

        // The agent-facing reads over a running `bobcat ui` (bobcat#524): JSON on stdout.
        factory.RegisterCommand<ListRunsCommand>();
        factory.RegisterCommand<RunStatusCommand>();
        factory.RegisterCommand<FailingTestsCommand>();
        factory.RegisterCommand<FailingSpecCommand>();
        factory.RegisterCommand<FlakyLedgerCommand>();
        factory.RegisterCommand<EventModelQueryCommand>();
        factory.RegisterCommand<SliceCoverageCommand>();
        factory.RegisterCommand<ExportRunCommand>();
        factory.RegisterCommand<AwaitRunCommand>();

        // Running a spec project and reading its specifications: run, preview, pick, watch
        factory.RegisterCommand<Specs.SpecRunCommand>();
        factory.RegisterCommand<Specs.SpecPreviewCommand>();
        factory.RegisterCommand<Specs.SpecPickCommand>();
        factory.RegisterCommand<Specs.SpecWatchCommand>();

        // A bare `bobcat` runs the spec project found from the current directory, and so does
        // `bobcat --feature …` or `bobcat path/to/Specs`. This is NOT the #369 trap above: that `run`
        // started an application host and blocked; this one finds a spec project, runs it and exits —
        // and with no spec project to find it fails fast, which is what a pipeline needs.
        factory.DefaultCommand = typeof(Specs.SpecRunCommand);
        factory.SetAppName(AppName);
    }

    /// <summary>
    /// The executor <c>Program</c> runs: every command above, dispatched through
    /// <see cref="UsageGuardedCommandFactory"/> so a usage mistake is one line and not a stack trace.
    /// </summary>
    public static CommandExecutor Executor(Action<CommandRun>? configureRun = null)
    {
        var factory = new CommandFactory();
        Register(factory);
        if (configureRun is not null) factory.ConfigureRun = configureRun;

        return new CommandExecutor(new UsageGuardedCommandFactory(factory));
    }
}

/// <summary>
/// JasperFx's <see cref="CommandFactory"/>, with one difference: a usage mistake is <b>one line on
/// standard error, a pointer to <c>bobcat help &lt;command&gt;</c>, and exit 1</b> (bobcat#785).
/// </summary>
/// <remarks>
/// <para>
/// JasperFx answers an unknown flag with a page of usage, and an exception from a command's
/// <c>Execute</c> — which is where "there is no project at '5'" surfaces when <c>--repeat 5</c>
/// hands <c>5</c> to the project argument — with the full stack trace. Neither is what a person who
/// mistyped a flag needs, and the <see cref="CommandExecutor"/> that prints them is sealed around
/// <see cref="ICommandFactory"/>, so this sits on that seam: the dispatch is <see cref="CommandFactory.BuildRun(IEnumerable{string})"/>'s
/// own, re-stated here only so that its two catch blocks can be ours.
/// </para>
/// <para>
/// Two kinds of usage error, one shape: a parse failure before the command runs (an unknown flag,
/// a value where none is expected, a flag with no value, a value of the wrong type), and an
/// <see cref="InvalidUsageException"/> thrown <i>by</i> the command — the tool's own code throws
/// that type for a mistake only it can see, such as a spec project that does not exist. Anything
/// else a command throws is still a defect and keeps its stack trace.
/// </para>
/// <para>
/// <c>help</c> and <c>?</c> go to JasperFx untouched, and so does a first word that is neither a
/// command nor an argument the default command takes — JasperFx lists the commands for that.
/// </para>
/// </remarks>
internal sealed class UsageGuardedCommandFactory : ICommandFactory
{
    private static readonly string[] helpCommands = ["help", "?"];

    private readonly CommandFactory _inner;
    private readonly ICommandCreator _creator = new ActivatorCommandCreator();
    private readonly Lazy<HashSet<string>> _names;

    public UsageGuardedCommandFactory(CommandFactory inner)
    {
        _inner = inner;
        _names = new Lazy<HashSet<string>>(() =>
            inner.AllCommandTypes().Select(CommandFactory.CommandNameFor).ToHashSet(StringComparer.Ordinal));
    }

    public CommandRun BuildRun(string commandLine) => BuildRun(StringTokenizer.Tokenize(commandLine));

    public CommandRun BuildRun(IEnumerable<string> args)
    {
        var tokens = ArgPreprocessor.Process(args).ToList();

        if (tokens.Count > 0 && helpCommands.Contains(tokens[0].ToLowerInvariant()))
        {
            return helpRun(args);
        }

        var queue = new Queue<string>(tokens);
        string commandName;
        if (queue.Count > 0 && _names.Value.Contains(queue.Peek().ToLowerInvariant()))
        {
            commandName = queue.Dequeue().ToLowerInvariant();
        }
        else if (_inner.DefaultCommand is { } fallback)
        {
            commandName = CommandFactory.CommandNameFor(fallback);
        }
        else
        {
            return helpRun(args);
        }

        var command = _inner.Build(commandName);

        object input;
        try
        {
            input = command.Usages.BuildInput(queue, _creator);
        }
        catch (InvalidUsageException e)
        {
            return UsageError.Run(commandName, e.Message);
        }
        catch (Exception e)
        {
            // A value the flag's type cannot read: `--retries abc`. JasperFx reports this one with
            // a stack trace too ("Error parsing input").
            return UsageError.Run(commandName, $"Could not read the arguments: {e.Message}");
        }

        var run = new CommandRun
        {
            Command = new UsageGuardedCommand(command, commandName),
            Input = input
        };

        _inner.ConfigureRun(run);

        return run;
    }

    /// <summary>
    /// JasperFx's own help, named for this tool: <see cref="CommandFactory.HelpRun(Queue{string})"/>
    /// leaves <see cref="HelpInput.AppName"/> at its <c>dotnet run --</c> default (the app name set on
    /// the factory reaches only its "not a command" page), and that is the page a usage error points at.
    /// </summary>
    private CommandRun helpRun(IEnumerable<string> args)
    {
        var run = _inner.BuildRun(args);
        if (run.Input is HelpInput help) help.AppName = BobcatCommands.AppName;
        return run;
    }

    public void RegisterCommands(System.Reflection.Assembly assembly) => _inner.RegisterCommands(assembly);

    public IEnumerable<IJasperFxCommand> BuildAllCommands() => _inner.BuildAllCommands();

    public void ApplyExtensions(Microsoft.Extensions.Hosting.IHostBuilder builder) => _inner.ApplyExtensions(builder);
}

/// <summary>
/// A command whose <see cref="InvalidUsageException"/> is a usage error — one line, exit 1 — rather
/// than a stack trace. Every other exception is a defect and passes through unchanged.
/// </summary>
internal sealed class UsageGuardedCommand : IJasperFxCommand
{
    private readonly IJasperFxCommand _inner;
    private readonly string _commandName;

    public UsageGuardedCommand(IJasperFxCommand inner, string commandName)
    {
        _inner = inner;
        _commandName = commandName;
    }

    public Type InputType => _inner.InputType;

    public UsageGraph Usages => _inner.Usages;

    public async Task<bool> Execute(object input)
    {
        try
        {
            return await _inner.Execute(input);
        }
        catch (InvalidUsageException e)
        {
            UsageError.Report(_commandName, e.Message);
            return false;
        }
    }
}

/// <summary>
/// How a usage mistake is told: <c>bobcat run: Unknown argument or flag for value --fetaure. See 'bobcat help run'.</c>
/// On standard error, so a <c>--json</c> pipe on standard output stays clean.
/// </summary>
internal static class UsageError
{
    /// <summary>A run that reports the mistake and fails, for a command that never got to run.</summary>
    public static CommandRun Run(string commandName, string message)
        => new()
        {
            Command = new ReportingCommand(commandName, message),
            Input = new object()
        };

    public static void Report(string commandName, string message)
        => System.Console.Error.WriteLine(Format(commandName, message));

    public static string Format(string commandName, string message)
    {
        var said = string.IsNullOrWhiteSpace(message) ? "Invalid usage" : message.Trim();
        if (!said.EndsWith('.') && !said.EndsWith(':')) said += ".";

        return $"{BobcatCommands.AppName} {commandName}: {said} See '{BobcatCommands.AppName} help {commandName}'.";
    }

    private sealed class ReportingCommand : IJasperFxCommand
    {
        private readonly string _commandName;
        private readonly string _message;

        public ReportingCommand(string commandName, string message)
        {
            _commandName = commandName;
            _message = message;
        }

        public Type InputType => typeof(object);

        // Never read: a usage error has no usage to show, and HelpCommand reads the command it names.
        public UsageGraph Usages => throw new NotSupportedException("A usage error has no usage graph.");

        public Task<bool> Execute(object input)
        {
            Report(_commandName, _message);
            return Task.FromResult(false);
        }
    }
}
