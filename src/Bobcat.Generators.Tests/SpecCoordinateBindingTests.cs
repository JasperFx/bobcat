using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#573, the generator half: each specification's Event Model coordinates — slice, chapter,
/// domain, pattern — are bound beside its test method, folded by the same calls the Event Model
/// descriptor is emitted from. That binding is the single source the manifest and the runner
/// adapters' traits read, so the pattern in it is the <em>derived</em> one too.
/// </summary>
public class SpecCoordinateBindingTests
{
    private const string Preamble =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Xunit;

        public record Deposit(Guid Id, decimal Amount);
        public class AccountSummary { public Guid Id { get; set; } }

        public abstract class ScenarioBase
        {
            [EventModelRole(SliceRole.Command, nameof(message))]
            public Task WhenReceived(object message) => Task.CompletedTask;

            [EventModelRole(SliceRole.ReadModel, nameof(T))]
            public Task ThenReadModel<T>(Guid id) where T : class => Task.CompletedTask;
        }

        """;

    [Fact]
    public void a_bound_specification_carries_its_slice_chapter_domain_and_derived_pattern()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Banking")]
            public class banking_specs : ScenarioBase
            {
                [BobcatSpec(SliceName = "Deposit funds", Chapter = "Money in", Domain = "Accounts")]
                public async Task a_deposit_is_accepted()
                {
                    await WhenReceived(new Deposit(Guid.NewGuid(), 10m));
                }
            }
            """);

        // Command is nowhere in the source: the act sends a command, so the slice is a Command slice,
        // which is what the Event Model says too
        outcome.GeneratedSource("DeclaredSteps").ShouldContain(
            "Bind(\"Banking/a deposit is accepted\", \"banking_specs\", \"a_deposit_is_accepted\", null, "
            + "\"Deposit funds\", \"Money in\", \"Accounts\", \"Command\");");
    }

    [Fact]
    public void a_slice_folded_from_several_specifications_gives_each_the_slice_s_values()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Account summary")]
            public class summary_specs : ScenarioBase
            {
                [BobcatSpec(typeof(AccountSummary), Chapter = "Reporting")]
                public async Task the_summary_shows_the_balance()
                {
                    await ThenReadModel<AccountSummary>(Guid.NewGuid());
                }

                [BobcatSpec(typeof(AccountSummary))]
                public async Task the_summary_shows_the_owner()
                {
                    await ThenReadModel<AccountSummary>(Guid.NewGuid());
                }
            }
            """);

        var source = outcome.GeneratedSource("DeclaredSteps");

        // The second spec wrote no chapter, but its slice has one, and the slice is what both are on
        source.ShouldContain("\"the_summary_shows_the_balance\", null, \"AccountSummary\", \"Reporting\", null, \"View\");");
        source.ShouldContain("\"the_summary_shows_the_owner\", null, \"AccountSummary\", \"Reporting\", null, \"View\");");
    }

    [Fact]
    public void a_declared_pattern_wins_over_inference()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Banking")]
            public class banking_specs : ScenarioBase
            {
                [BobcatSpec(SliceName = "Sweep", Pattern = "Automation")]
                public async Task a_sweep_moves_money()
                {
                    await WhenReceived(new Deposit(Guid.NewGuid(), 10m));
                }
            }
            """);

        outcome.GeneratedSource("DeclaredSteps").ShouldContain("\"a_sweep_moves_money\", null, \"Sweep\", null, null, \"Automation\");");
    }

    [Fact]
    public void a_specification_bound_to_no_slice_is_bound_without_coordinates()
    {
        var outcome = GeneratorHarness.Run(Preamble +
            """
            [BobcatFeature("Plain")]
            public class plain_specs
            {
                [BobcatSpec]
                public void nothing_about_a_model()
                {
                    // Given nothing in particular
                }
            }
            """);

        outcome.GeneratedSource("DeclaredSteps")
            .ShouldContain("Bind(\"Plain/nothing about a model\", \"plain_specs\", \"nothing_about_a_model\");");
    }
}
