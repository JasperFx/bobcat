using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace Bobcat.Mtp;

/// <summary>
/// Registers <c>--filter-feature</c> and <c>--filter-tag</c> with the platform's command line,
/// so <c>dotnet test -- --filter-feature "Calculator"</c> and <c>--filter-tag regression</c>
/// work against a Bobcat host the way <c>--filter-class</c>/<c>--filter-method</c> do against
/// an xUnit v3 one. The matching semantics live in <see cref="SpecFilters"/>.
/// </summary>
public sealed class BobcatFilterOptionsProvider : ICommandLineOptionsProvider
{
    public string Uid => nameof(BobcatFilterOptionsProvider);
    public string Version => typeof(BobcatFilterOptionsProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    public string DisplayName => "Bobcat filters";
    public string Description => "Filters a Bobcat spec run by feature title or Gherkin tag.";

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() =>
    [
        new CommandLineOption(
            SpecFilters.FeatureOption,
            "Run only scenarios whose feature title contains the given text (case-insensitive).",
            ArgumentArity.ExactlyOne,
            isHidden: false),
        new CommandLineOption(
            SpecFilters.TagOption,
            "Run only scenarios carrying the given Gherkin tag, written without the leading @ (case-insensitive).",
            ArgumentArity.ExactlyOne,
            isHidden: false),
    ];

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
        => arguments.Length == 1 && !string.IsNullOrWhiteSpace(arguments[0])
            ? ValidationResult.ValidTask
            : ValidationResult.InvalidTask($"--{commandOption.Name} needs a non-empty value.");

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions)
        => ValidationResult.ValidTask;
}
