using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Bobcat.Marten.Tests;

/// <summary>
/// Issue #232, measured against a real Marten: <b>"the scaffold compiles" is the wrong bar,
/// "the host boots" is the bar.</b>
/// </summary>
/// <remarks>
/// A scaffolded View slice used to emit a projection whose body was two TODO comments. It
/// compiled perfectly and could not be <em>registered</em>: Marten validates at startup that a
/// projection has at least one conventional method, so the host resource never started and all
/// eleven scenarios of the chapter reported <c>did not run</c> — two unfilled View slices taking
/// down nine other slices' specs, the runtime form of the "one hole fails everything" property
/// #226 had just removed from the compile side.
///
/// These are the shapes <c>ViewSliceFrame</c> emits, and the shapes it used to. They are pinned
/// here rather than reasoned about because the first attempt at the fix was wrong in a way only
/// a real store could tell us: adding an <c>Apply</c> is enough for a single-stream projection
/// and <em>not</em> for a fan-out, which then fails registration for a different reason.
/// </remarks>
public class ProjectionRegistrationTests
{
    public record AppointmentProposed(Guid AppointmentId, string Kennel);

    public class AppointmentsQueue
    {
        public Guid Id { get; set; }
    }

    /// <summary>What a scaffolded fan-out used to be: no conventional method at all.</summary>
    public class EmptyFanOutProjection : MultiStreamProjection<AppointmentsQueue, Guid>;

    /// <summary>What a scaffolded single-stream projection used to be.</summary>
    public class EmptySingleProjection : SingleStreamProjection<AppointmentsQueue, Guid>;

    /// <summary>An Apply and no slicing rule — the near miss.</summary>
    public class UnslicedFanOutProjection : MultiStreamProjection<AppointmentsQueue, Guid>
    {
        public void Apply(AppointmentProposed proposed, AppointmentsQueue view)
            => throw new NotImplementedException("TODO: AppointmentsQueue — project AppointmentProposed");
    }

    /// <summary>What <c>ViewSliceFrame</c> emits for <c>fanOut: true</c> now.</summary>
    public class ScaffoldedFanOutProjection : MultiStreamProjection<AppointmentsQueue, Guid>
    {
        public ScaffoldedFanOutProjection()
        {
            Identity<AppointmentProposed>(x => x.AppointmentId);
        }

        public void Apply(AppointmentProposed proposed, AppointmentsQueue view)
            => throw new NotImplementedException("TODO: AppointmentsQueue — project AppointmentProposed");
    }

    /// <summary>What it emits otherwise.</summary>
    public class ScaffoldedSingleProjection : SingleStreamProjection<AppointmentsQueue, Guid>
    {
        public void Apply(AppointmentProposed proposed, AppointmentsQueue view)
            => throw new NotImplementedException("TODO: AppointmentsQueue — project AppointmentProposed");
    }

    /// <summary>The marker a fan-out routes by (issue #347).</summary>
    public interface IAppointmentEvent
    {
        Guid AppointmentId { get; }
    }

    public record MarkedAppointmentProposed(Guid AppointmentId, string Kennel) : IAppointmentEvent;

    /// <summary>What <c>ViewSliceFrame</c> emits for a fan-out whose sources share one key.</summary>
    public class EvolvingFanOutProjection : MultiStreamProjection<AppointmentsQueue, Guid>
    {
        public EvolvingFanOutProjection() => Identity<IAppointmentEvent>(x => x.AppointmentId);

        // Nullable parameter, matching both the base and what ViewSliceFrame emits — this fixture
        // is only worth anything while it stays a faithful copy of the scaffolded shape.
        public override AppointmentsQueue Evolve(AppointmentsQueue? snapshot, Guid id, IEvent e)
        {
            snapshot ??= new AppointmentsQueue { Id = id };
            return snapshot;
        }
    }

    /// <summary>
    /// The shape 0.26.1 shipped (#351): a STATIC Evolve taking the marker. It compiles, and it is
    /// not a conventional method — which is the whole point of pinning it here.
    /// </summary>
    public class StaticEvolveFanOutProjection : MultiStreamProjection<AppointmentsQueue, Guid>
    {
        public StaticEvolveFanOutProjection() => Identity<IAppointmentEvent>(x => x.AppointmentId);

        public static AppointmentsQueue Evolve(AppointmentsQueue view, IAppointmentEvent e) => view;
    }

    /// <summary>Registers one projection the way the scaffold's own guidance says to, and boots.</summary>
    private static async Task<Exception?> boot<T>()
        where T : ProjectionBase, IProjectionSource<IDocumentOperations, IQuerySession>, new()
    {
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .ConfigureServices(services => services.AddMarten(opts =>
                    {
                        opts.Connection(PostgresEnvironment.ConnectionString);
                        opts.DatabaseSchemaName = "registration_" + typeof(T).Name.ToLowerInvariant();
                        opts.Projections.Add<T>(ProjectionLifecycle.Inline);
                    })
                    .ApplyAllDatabaseChangesOnStartup())
                .StartAsync();

            await host.StopAsync();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [PostgresFact]
    public async Task the_shapes_the_scaffolder_emits_can_be_registered()
    {
        (await boot<ScaffoldedSingleProjection>()).ShouldBeNull();
        (await boot<ScaffoldedFanOutProjection>()).ShouldBeNull();
        (await boot<EvolvingFanOutProjection>()).ShouldBeNull();
    }

    [PostgresFact]
    public async Task a_static_evolve_is_not_a_conventional_method()
    {
        // 0.26.1 scaffolded exactly this and shipped it. It compiles, the scaffolding suite's text
        // assertions passed, and the host does not boot — so every scenario in a generated repo
        // died before a step ran. Text tests cannot see this; only booting can.
        var failure = await boot<StaticEvolveFanOutProjection>();

        failure.ShouldNotBeNull();
        failure.Message.ShouldContain("No matching conventional Apply/Create/ShouldDelete methods");
    }

    [PostgresFact]
    public async Task an_empty_projection_stops_the_host_booting()
    {
        // The reported failure, verbatim — and it is a HOST failure, so it is not the View
        // slice's own scenarios that fail, it is every scenario in the suite.
        foreach (var failure in new[] { await boot<EmptyFanOutProjection>(), await boot<EmptySingleProjection>() })
        {
            failure.ShouldNotBeNull();
            failure.Message.ShouldContain("No matching conventional Apply/Create/ShouldDelete methods");
        }
    }

    [PostgresFact]
    public async Task an_apply_alone_is_not_enough_for_a_fan_out()
    {
        // Why ViewSliceFrame emits the Identity rule as well: adding a conventional method to a
        // multi-stream projection trades one registration failure for another, and a scaffold
        // that swapped one dead host for a different dead host would have fixed nothing.
        var failure = await boot<UnslicedFanOutProjection>();

        failure.ShouldNotBeNull();
        failure.Message.ShouldContain("has no defined event slicing rules");
    }
}
