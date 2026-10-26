using Bobcat.Console.Specs;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Help;
using JasperFx.CommandLine.Parsing;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// bobcat#785: no short flag may mean two things. JasperFx aliases every flag to its first letter
/// unless the flag says otherwise, so two flags starting with the same letter on one command silently
/// collide and whichever handler comes first wins. Flags are public API at 1.0, so this is pinned
/// against the tool's own registration rather than left to a manual walkthrough.
/// </summary>
/// <remarks>
/// The handlers are read off each command's <see cref="UsageGraph"/>, which is what parses a real
/// command line. They are JasperFx's <i>source-generated</i> handlers (the JasperFx package carries
/// the generator), which keep their <see cref="FlagAliases"/> private and expose no member — so the
/// aliases are read back from <see cref="ITokenHandler.ToUsageDescription"/>, whose first word or two
/// is <c>FlagAliases.ToString()</c> (<c>-w, --workers</c> or <c>--workers</c>) for the generated and
/// the reflection handlers alike. <see cref="the_parser_refuses_a_short_flag_that_went_long_only"/>
/// checks the real parser agrees.
/// </remarks>
public class ShortFlagTests
{
    private static IReadOnlyList<Type> commands()
    {
        var factory = new CommandFactory();
        BobcatCommands.Register(factory);
        return factory.AllCommandTypes().ToList();
    }

    private static Type commandNamed(string command)
        => commands().Single(x => CommandFactory.CommandNameFor(x) == command);

    /// <summary>Every flag of a command with the aliases its handler answers to.</summary>
    private static List<(string Member, FlagAliases Aliases)> flagsOf(Type command)
        => new UsageGraph(command).Flags.Select(x => (x.MemberName, AliasesOf(x))).ToList();

    /// <summary>
    /// <c>[-w, --workers &lt;workers&gt;]</c> → <c>-w</c> / <c>--workers</c>; <c>[--failed]</c> → long-only.
    /// </summary>
    internal static FlagAliases AliasesOf(ITokenHandler handler)
    {
        var words = handler.ToUsageDescription().TrimStart('[').Split(' ', 3);

        return words[0].EndsWith(',')
            ? new FlagAliases { ShortForm = words[0].TrimEnd(','), LongForm = words[1].TrimEnd(']') }
            : new FlagAliases { ShortForm = "", LongForm = words[0].TrimEnd(']'), LongFormOnly = true };
    }

    private static (string Member, FlagAliases Aliases) flagOf(string command, string longForm)
    {
        var flags = flagsOf(commandNamed(command));
        var flag = flags.SingleOrDefault(x => x.Aliases.LongForm == longForm);

        flag.Aliases.ShouldNotBeNull($"{command} has no {longForm}; its flags are {string.Join(", ", flags.Select(x => x.Aliases.LongForm))}");
        return flag;
    }

    [Fact]
    public void the_tool_registers_the_commands_it_documents()
    {
        commands().Select(CommandFactory.CommandNameFor).ShouldBe(
        [
            "import-event-model", "resident", "ui", "watch-event-model", "generate-ui-contracts",
            "results-schema", "results-html", "list-runs", "run-status", "failing-tests", "failing-spec",
            "flaky-ledger", "event-model", "slice-coverage", "export-run", "await-run",
            "run", "preview", "pick", "watch"
        ], ignoreOrder: true);
    }

    [Fact]
    public void every_command_has_flags_to_check()
    {
        // The guard below passes vacuously over a command whose flags could not be read
        foreach (var command in commands().Where(x => new UsageGraph(x).Flags.Any()))
        {
            flagsOf(command).ShouldAllBe(x => x.Aliases.LongForm.StartsWith("--"), CommandFactory.CommandNameFor(command));
        }

        flagsOf(commandNamed("run")).Count.ShouldBeGreaterThan(15);
    }

    [Fact]
    public void no_command_declares_the_same_short_alias_twice()
    {
        var collisions = new List<string>();

        foreach (var command in commands())
        {
            var byShortForm = flagsOf(command)
                .Where(x => !x.Aliases.LongFormOnly)
                .GroupBy(x => x.Aliases.ShortForm)
                .Where(g => g.Count() > 1);

            foreach (var group in byShortForm)
            {
                collisions.Add(
                    $"{CommandFactory.CommandNameFor(command)}: {group.Key} is {string.Join(" and ", group.Select(x => x.Aliases.LongForm))}");
            }
        }

        collisions.ShouldBeEmpty(
            "A short flag means two things. Make the colliding flags long-only with [FlagAlias(\"name\", true)]:"
            + Environment.NewLine + string.Join(Environment.NewLine, collisions));
    }

    public static TheoryData<string, string> LongOnlyFlags => new()
    {
        // run, preview, pick, watch: -f, -r, -s and -c each meant several things
        { "run", "--failed" }, { "run", "--framework" }, { "run", "--feature" },
        { "run", "--repeat" }, { "run", "--retries" }, { "run", "--results" },
        { "run", "--stall" }, { "run", "--stall-action" }, { "run", "--scenario" }, { "run", "--slice" },
        { "run", "--configuration" }, { "run", "--chapter" },
        { "preview", "--framework" }, { "preview", "--feature" }, { "preview", "--configuration" }, { "preview", "--chapter" },
        { "watch", "--scenario" }, { "watch", "--slice" }, { "watch", "--results" },
        // results-schema: --baseline and --baseline-name were both -b
        { "results-schema", "--baseline" }, { "results-schema", "--baseline-name" },
        // resident takes the same spellings as run for a .csproj
        { "resident", "--framework" }, { "resident", "--configuration" }
    };

    [Theory]
    [MemberData(nameof(LongOnlyFlags))]
    public void a_flag_that_collided_is_long_only(string command, string longForm)
    {
        var flag = flagOf(command, longForm);

        flag.Aliases.LongFormOnly.ShouldBeTrue($"{command} {longForm} answers to {flag.Aliases.ShortForm} too");
    }

    [Theory]
    [InlineData("run", "-w", "--workers")]
    [InlineData("run", "-d", "--domain")]
    [InlineData("ui", "-p", "--port")]
    [InlineData("resident", "-u", "--url")]
    [InlineData("export-run", "-f", "--format")]
    [InlineData("import-event-model", "-o", "--out")]
    public void a_short_alias_stays_where_it_is_unique(string command, string shortForm, string longForm)
    {
        var flag = flagOf(command, longForm);

        flag.Aliases.LongFormOnly.ShouldBeFalse();
        flag.Aliases.ShortForm.ShouldBe(shortForm);
    }

    [Theory]
    [InlineData("-f")]
    [InlineData("-r")]
    [InlineData("-s")]
    [InlineData("-c")]
    public void the_parser_refuses_a_short_flag_that_went_long_only(string shortFlag)
    {
        // The command's own graph, which carries the usages its constructor declares
        var usages = new SpecRunCommand().Usages;

        var refusal = Should.Throw<InvalidUsageException>(
            () => usages.BuildInput(new Queue<string>([shortFlag, "x"]), new ActivatorCommandCreator()));

        refusal.Message.ShouldBe($"Unknown argument or flag for value {shortFlag}");
    }

    [Fact]
    public void the_parser_takes_the_long_forms_and_the_unique_short_ones()
    {
        var usages = new SpecRunCommand().Usages;

        var input = (SpecRunInput)usages.BuildInput(
            new Queue<string>(["--framework", "net10.0", "--feature", "Booking", "--failed", "-w", "2", "-d", "Orders"]),
            new ActivatorCommandCreator());

        input.FrameworkFlag.ShouldBe("net10.0");
        input.FeatureFlag.ShouldBe(["Booking"]);
        input.FailedFlag.ShouldBeTrue();
        input.WorkersFlag.ShouldBe(2);
        input.DomainFlag.ShouldBe(["Orders"]);
    }
}
