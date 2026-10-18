using Microsoft.CodeAnalysis;

namespace Bobcat.Generators.Tests;

/// <summary>
/// A stand-in for WolverineFx.Bobcat's store vocabulary, compiled into an assembly of its own: since bobcat#744
/// <c>Bobcat.CritterStack.CritterStackScenario</c> and <c>CritterStackSpec</c> ship from the Wolverine repo
/// under the same names, and the generator still recognises them by metadata name (as it does Wolverine.Http's
/// endpoint attributes) and reads their role attributes from metadata. The few members here carry the roles
/// the real ones do.
/// </summary>
internal static class CritterStackStandIn
{
    private const string Source =
        """
        using System;
        using System.Threading.Tasks;
        using Bobcat;

        namespace Bobcat.CritterStack
        {
            public class CritterStackScenario
            {
                public CritterStackScenario(IServiceProvider services) { }

                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenEvents<TAggregate>(object id, params object[] events) where TAggregate : class => Task.CompletedTask;

                [EventModelRole(SliceRole.Document, nameof(T))]
                public Task<T> ThenDocument<T>(object id) where T : class => Task.FromResult<T>(null!);
            }

            public abstract class CritterStackSpec
            {
                private readonly CritterStackScenario _scenario;

                protected CritterStackSpec(IServiceProvider services) => _scenario = new CritterStackScenario(services);

                [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]
                [EventModelRole(SliceRole.ArrangedEvent, nameof(events))]
                public Task GivenEvents<TAggregate>(object id, params object[] events) where TAggregate : class
                    => _scenario.GivenEvents<TAggregate>(id, events);

                [EventModelRole(SliceRole.Document, nameof(T))]
                public Task<T> ThenDocument<T>(object id) where T : class => _scenario.ThenDocument<T>(id);
            }
        }
        """;

    private static readonly Lazy<MetadataReference> reference = new(() =>
        MetadataReference.CreateFromImage(GeneratorHarness.CompileLibrary("WolverineFx.Bobcat.StandIn", Source)));

    /// <summary>The compiled stand-in, to pass as an extra reference.</summary>
    public static MetadataReference Reference => reference.Value;
}
