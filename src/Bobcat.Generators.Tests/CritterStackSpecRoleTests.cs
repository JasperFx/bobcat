using System.Reflection;
using Bobcat.CritterStack;
using JasperFx.Events.EventModeling;
using Shouldly;
using static Bobcat.Generators.Tests.ProjectedEventModelRoleTests;

namespace Bobcat.Generators.Tests;

/// <summary>
/// bobcat#471: the store-only <see cref="CritterStackScenario" /> and its <see cref="CritterStackSpec" />
/// forwarding twins carry <c>[EventModelRole]</c>, so a projected spec written against the real vocabulary —
/// read from the Bobcat assembly's metadata, as a consumer's build reads it — reaches the Event Model with
/// its aggregates, events, arranged events, read models, documents and messages.
/// </summary>
public class CritterStackSpecRoleTests
{
    private const string Source =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;
        using Bobcat.CritterStack;
        using Xunit;

        namespace Bank
        {
            public record AccountOpened(Guid Id, string Owner);
            public record FundsDeposited(Guid Id, decimal Amount);
            public record FundsWithdrawn(Guid Id, decimal Amount);
            public record DepositReceipt(Guid Id);
            public class Account { public Guid Id { get; set; } }
            public class AccountSummary { public Guid Id { get; set; } }
            public class Statement { public Guid Id { get; set; } }
        }

        namespace Specs
        {
            using Bank;

            [BobcatFeature("Accounts")]
            public class accounts : CritterStackSpec
            {
                public accounts() : base((IServiceProvider)null!) { }

                [Fact, BobcatSlice(SliceName = "Deposit")]
                public async Task a_deposit_is_credited()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents<Account>(id, new AccountOpened(id, "Ann"));
                    await WhenActing("a deposit is made", _ => Task.CompletedTask);
                    ThenEvents(typeof(FundsDeposited));
                    await ThenAggregate<Account>(id);
                    await ThenDocument<Statement>(id);
                    ThenMessagesSent<DepositReceipt>();

                    // Negatives stamp nothing
                    ThenNotEmitted<FundsWithdrawn>();
                }

                [Fact, BobcatSlice(SliceName = "Withdraw")]
                public async Task a_withdrawal_acting_on_the_stream()
                {
                    var id = Guid.NewGuid();
                    await GivenEvents(typeof(Account), id, new AccountOpened(id, "Ann"));
                    await WhenActingOn<Account>(id, "a withdrawal", _ => Task.CompletedTask, new FundsWithdrawn(id, 1m));
                }
            }

            [BobcatFeature("Summaries")]
            [BobcatSlice(SliceName = "AccountSummary")]
            public class account_summary : CritterStackSpec
            {
                public account_summary() : base((IServiceProvider)null!) { }

                [Fact]
                public async Task the_summary_shows_the_balance()
                {
                    var id = Guid.NewGuid();
                    await GivenEventsOn<Account>(id, new AccountOpened(id, "Ann"), new FundsDeposited(id, 10m));
                    await ThenReadModel<AccountSummary>(id);
                    await ThenSingleDocument<Statement>();
                }
            }
        }
        """;

    [Fact]
    public void a_projected_critter_stack_spec_reaches_the_event_model_with_its_roles()
    {
        var outcome = GeneratorHarness.Run(Source);
        outcome.CompilationErrors.ShouldBeEmpty();

        var slices = GeneratedEventModel.For(load(outcome))!.Slices;

        var deposit = slices.Single(x => x.Name == "Deposit");
        deposit.CommandType.ShouldBeNull();
        names(deposit.AggregateTypes).ShouldBe(["Account"]);
        names(deposit.EmittedEvents).ShouldBe(["FundsDeposited"]);
        names(deposit.PublishedMessages).ShouldBe(["DepositReceipt"]);
        names(deposit.ReadModelTypes).ShouldBeEmpty();
        names(deposit.Specifications.ShouldHaveSingleItem().ResolvedTypes)
            .ShouldBe(["Account", "AccountOpened", "FundsDeposited", "Statement", "DepositReceipt"]);

        var withdraw = slices.Single(x => x.Name == "Withdraw");
        names(withdraw.AggregateTypes).ShouldBe(["Account"]);
        names(withdraw.EmittedEvents).ShouldBe(["FundsWithdrawn"]);
        names(withdraw.Specifications.ShouldHaveSingleItem().ResolvedTypes)
            .ShouldBe(["Account", "AccountOpened", "FundsWithdrawn"]);

        var summary = slices.Single(x => x.Name == "AccountSummary");
        summary.Pattern.ShouldBe(SlicePattern.View);
        names(summary.AggregateTypes).ShouldBe(["Account"]);
        names(summary.ReadModelTypes).ShouldBe(["AccountSummary"]);
        names(summary.ConsumedEvents).ShouldBe(["AccountOpened", "FundsDeposited"]);

        // A document is resolved, never a slot on the slice
        names(summary.Specifications.ShouldHaveSingleItem().ResolvedTypes).ShouldContain("Statement");
    }

    [Fact]
    public void every_spec_twin_declares_the_same_roles_as_the_scenario_method_it_forwards_to()
    {
        var scenario = rolesByMethod(typeof(CritterStackScenario));
        var spec = rolesByMethod(typeof(CritterStackSpec));

        spec.ShouldNotBeEmpty();
        foreach (var (signature, roles) in spec)
        {
            scenario.ShouldContainKey(signature);
            scenario[signature].ShouldBe(roles, $"the roles on CritterStackSpec.{signature}");
        }

        // The only role-bearing scenario overloads without a Spec twin
        scenario.Keys.Except(spec.Keys).Order().ShouldBe([
            "GivenEventsOn(Type, Object, Object[])",
            "ThenStreamIsStarted(Type, Object)"
        ]);
    }

    [Fact]
    public void every_role_names_a_parameter_of_its_method()
    {
        // BOBCAT034 runs on a consumer's source, not on Bobcat's own; nameof already keeps these honest
        foreach (var type in new[] { typeof(CritterStackScenario), typeof(CritterStackSpec) })
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        foreach (var role in method.GetCustomAttributes<EventModelRoleAttribute>())
        {
            var names = method.GetParameters().Select(x => x.Name)
                .Concat(method.IsGenericMethod ? method.GetGenericArguments().Select(x => x.Name) : []);
            names.ShouldContain(role.From, $"{type.Name}.{method.Name}");
        }
    }

    private static Dictionary<string, string[]> rolesByMethod(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Roles: m.GetCustomAttributes<EventModelRoleAttribute>()
                .Select(r => $"{r.Role}:{r.From}").Order().ToArray()))
            .Where(x => x.Roles.Length > 0)
            .ToDictionary(x => signatureOf(x.Method), x => x.Roles);

    private static string signatureOf(MethodInfo method)
    {
        var generic = method.IsGenericMethod ? $"<{string.Join(", ", method.GetGenericArguments().Select(x => x.Name))}>" : "";
        return $"{method.Name}{generic}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
    }
}
