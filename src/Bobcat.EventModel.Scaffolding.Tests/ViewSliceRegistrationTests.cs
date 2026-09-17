using Bobcat.EventModel;
using Shouldly;

namespace Bobcat.EventModel.Scaffolding.Tests;

/// <summary>
/// The issue #232 acceptance fixture: a scaffolded projection is <b>registrable</b>, not merely
/// compilable.
/// </summary>
/// <remarks>
/// The app project compiled, the spec project built, all eleven scenarios were discovered — and
/// then every one of them failed before a step ran, with <c>Resource 'Program' failed to start:
/// No matching conventional Apply/Create/ShouldDelete methods</c>. Two unfilled View slices took
/// down nine other slices' specs: the runtime form of the "one hole fails everything" property
/// #226 had just removed from the compile side.
///
/// What actually registers was measured against a real Marten rather than reasoned about — see
/// <c>Bobcat.Marten.Tests.ProjectionRegistrationTests</c>, which pins both halves: an
/// <c>Apply</c> is enough for a single-stream projection, and a fan-out needs an
/// <c>Identity</c> slicing rule too or it swaps one dead host for another.
/// </remarks>
public class ViewSliceRegistrationTests
{
    /// <summary>Two source events keying by different fields — no one interface can say that.</summary>
    internal const string MixedKeyYaml =
        """
        schema: 1
        model: Mixed
        namespace: Mixed
        slices:
          - name: DoThing
            pattern: Command
            domain: Things
            trigger: { kind: MessageHandler }
            command: DoThing
            aggregates: [Thing]
            events: [ThingHappened]
            elements:
              ThingHappened:
                fields: { thingId: Guid }
          - name: DoOther
            pattern: Command
            domain: Things
            trigger: { kind: MessageHandler }
            command: DoOther
            aggregates: [Thing]
            events: [OtherHappened]
            elements:
              OtherHappened:
                fields: { otherId: Guid }
          - name: MixedQueue
            pattern: View
            domain: Things
            projections: [MixedQueueProjection]
            fanOut: true
            readModels: [MixedQueue]
            consumes: [ThingHappened, OtherHappened]
        """;

    internal const string ModelYaml =
        """
        schema: 1
        model: CritterCrush
        namespace: CritterCrush
        slices:
          - name: ProposeHomeCheckAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: MessageHandler }
            command: ProposeHomeCheckAppointment
            aggregates: [Appointment]
            events: [HomeCheckAppointmentProposed]
            elements:
              HomeCheckAppointmentProposed:
                fields: { appointmentId: Guid, kennel: string }
          - name: ConfirmAppointment
            pattern: Command
            domain: Appointments
            trigger: { kind: MessageHandler }
            command: ConfirmAppointment
            aggregates: [Appointment]
            events: [AppointmentConfirmed]
            elements:
              AppointmentConfirmed:
                fields: { appointmentId: Guid }
          # The View slice that took the host down: no events of its own, fanOut declared.
          - name: AppointmentsQueue
            pattern: View
            domain: Appointments
            projections: [AppointmentsQueueProjection]
            fanOut: true
            readModels: [AppointmentsQueue]
          # Single-stream: the same requirement, one rule less.
          - name: AppointmentDetail
            pattern: View
            domain: Appointments
            projections: [AppointmentDetailProjection]
            readModels: [AppointmentDetail]
        """;

    /// <summary>A model whose events carry no identifier at all — nothing a fan-out can slice by.</summary>
    private const string UnroutableYaml =
        """
        schema: 1
        model: Sparse
        namespace: Sparse
        slices:
          - name: RingBell
            pattern: Command
            domain: Hall
            trigger: { kind: MessageHandler }
            command: RingBell
            aggregates: [Hall]
            events: [BellRang]
          - name: BellLog
            pattern: View
            domain: Hall
            projections: [BellLogProjection]
            fanOut: true
            readModels: [BellLog]
        """;

    /// <summary>And one with no events anywhere, so there is nothing to fold.</summary>
    private const string EventlessYaml =
        """
        schema: 1
        model: Empty
        namespace: Empty
        slices:
          - name: Nothing
            pattern: View
            domain: Void
            projections: [NothingProjection]
            readModels: [Nothing]
        """;

    private static CuratedModelFile parse(string yaml)
    {
        var reading = CuratedModelReader.Read(yaml);
        reading.Problems.ShouldBeEmpty();
        return reading.File!;
    }

    private static string scaffold(string yaml, string sliceName)
    {
        var model = parse(yaml);
        return SliceScaffolder.Scaffold(model, model.Slices.Single(x => x.Name == sliceName)).Single().Value;
    }

    [Fact]
    public void a_fan_out_projection_carries_both_the_slicing_rule_and_a_conventional_method()
    {
        var code = scaffold(ModelYaml, "AppointmentsQueue");

        // Both halves, because Marten refuses the projection for a different reason without each.
        code.ShouldContain("public class AppointmentsQueueProjection : MultiStreamProjection<AppointmentsQueue, Guid>");

        // One slicing rule and one fold, not one of each per event (issue #347): every source keys
        // by AppointmentId, so that agreement IS the interface.
        code.ShouldContain("public interface IAppointmentEvent");
        code.ShouldContain("Guid AppointmentId { get; }");
        code.ShouldContain("Identity<IAppointmentEvent>(x => x.AppointmentId);");
        // The BASE CLASS's virtual taking IEvent. A static Evolve(view, IMarker) compiles and does
        // NOT register — "No matching conventional Apply/Create/ShouldDelete methods" at startup,
        // which is this fixture's whole subject. 0.26.1 shipped that shape (#351); asserting the
        // signature is how this file stops it coming back.
        code.ShouldContain("public override AppointmentsQueue Evolve(AppointmentsQueue snapshot, Guid id, IEvent e)");
        code.ShouldContain("snapshot ??= new AppointmentsQueue { Id = id };");
        code.ShouldContain("if (e.Data is IAppointmentEvent routed) snapshot.AppointmentId = routed.AppointmentId;");
        code.ShouldContain("case HomeCheckAppointmentProposed:");
        code.ShouldNotContain("public static AppointmentsQueue Evolve");

        // Its sources are the domain's events, because the View slice declares none of its own —
        // so the fold covers them too.
        code.ShouldContain("case AppointmentConfirmed:");

        // And the shape it replaced is gone, rather than emitted beside it.
        code.ShouldNotContain("Identity<HomeCheckAppointmentProposed>");
        code.ShouldNotContain("public void Apply(HomeCheckAppointmentProposed");
    }

    [Fact]
    public void the_marker_is_declared_once_and_stamped_on_the_records_that_carry_it()
    {
        // The two halves live in different files: the interface with the view that asks the
        // routing question, the marker on records owned by the slices that EMIT them.
        var model = parse(ModelYaml);
        var emitter = SliceScaffolder.Scaffold(model, model.Slices.Single(x => x.Name == "ConfirmAppointment"))
            .Single().Value;

        emitter.ShouldContain(") : IAppointmentEvent;");
        emitter.ShouldNotContain("public interface IAppointmentEvent");
    }

    [Fact]
    public void sources_that_key_differently_keep_the_per_event_shape()
    {
        // Two keys cannot be one interface, and a marker over the majority would leave a reader
        // unable to tell from the constructor which events it covers. So: no marker at all.
        var model = parse(MixedKeyYaml);
        var code = SliceScaffolder.Scaffold(model, model.Slices.Single(x => x.Name == "MixedQueue"))
            .Single().Value;

        code.ShouldNotContain("public interface I");
        code.ShouldNotContain("Evolve(");
        code.ShouldNotContain("e.Data is");
        code.ShouldContain("Identity<ThingHappened>(x => x.ThingId);");
        code.ShouldContain("Identity<OtherHappened>(x => x.OtherId);");
        code.ShouldContain("public void Apply(ThingHappened thingHappened, MixedQueue view)");
    }

    [Fact]
    public void a_single_stream_projection_needs_only_the_conventional_method()
    {
        var code = scaffold(ModelYaml, "AppointmentDetail");

        code.ShouldContain("public class AppointmentDetailProjection : SingleStreamProjection<AppointmentDetail, Guid>");
        code.ShouldContain("public void Apply(HomeCheckAppointmentProposed homeCheckAppointmentProposed, AppointmentDetail view)");
        code.ShouldNotContain("Identity<");
    }

    [Fact]
    public void a_fan_out_the_model_cannot_route_degrades_to_single_stream_rather_than_to_a_dead_host()
    {
        // `fanOut: true` is a hint, and honouring it here would mean emitting either a slicing
        // rule over a field that does not exist (a compile error) or none at all (an unregistrable
        // projection). Single-stream boots, and the comment says what to do about it.
        var code = scaffold(UnroutableYaml, "BellLog");

        code.ShouldContain("public class BellLogProjection : SingleStreamProjection<BellLog, Guid>");
        code.ShouldContain("names no Guid field on any source event to slice");
        code.ShouldContain("public void Apply(BellRang bellRang, BellLog view)");
    }

    [Fact]
    public void a_source_event_with_no_identifier_is_left_out_of_the_slicing_and_said_so()
    {
        // AppointmentConfirmed carries an id; a hypothetical event without one cannot be routed,
        // and quietly dropping it would leave a read model that never sees it with no explanation.
        var model = parse(ModelYaml);
        model.Slices.Single(x => x.Name == "ConfirmAppointment").Elements.Clear();

        var slice = model.Slices.Single(x => x.Name == "AppointmentsQueue");
        var code = ScaffoldFrame.Render(new ViewSliceFrame(slice, SliceScaffolder.ViewSourcesFor(model, slice)));

        code.ShouldContain("Not sliced here: AppointmentConfirmed");
        code.ShouldNotContain("Identity<AppointmentConfirmed>");
    }

    [Fact]
    public void a_declared_consumed_events_list_is_the_projections_source_set_over_any_inference()
    {
        // Issue #297: `consumedEvents:` says outright what the view applies — the emlang import
        // writes it and a curated file may declare it — so the aggregate/domain/model inference
        // is only for a slice that says nothing.
        var model = parse(ModelYaml);
        var slice = model.Slices.Single(x => x.Name == "AppointmentsQueue");
        slice.ConsumedEvents.Add("AppointmentConfirmed");

        var sources = SliceScaffolder.ViewSourcesFor(model, slice);

        sources.Select(x => x.Event).ShouldBe(["AppointmentConfirmed"]);
        sources.Single().IdentityField.ShouldBe("AppointmentId");
    }

    [Fact]
    public void a_model_with_no_events_emits_no_projection_at_all()
    {
        // The one case where there is genuinely nothing to fold. A projection class here could
        // only be an unregistrable one, so the read model and its endpoint ship without it.
        var code = scaffold(EventlessYaml, "Nothing");

        code.ShouldContain("public class Nothing");
        code.ShouldContain("cannot be registered — it would stop the host booting");
        code.ShouldNotContain("class NothingProjection");
    }

    [Fact]
    public void two_slices_naming_one_event_declare_its_record_once()
    {
        // Not the boot failure, the compile one just under it: the importer folds slices by
        // command, so two slices may legally name the same event, and a record emitted into both
        // files is two declarations of one type in one namespace. Same rule as an aggregate
        // (#222) and a trigger contract (#223) — first declaring slice in model order owns it.
        var yaml = ModelYaml.Replace("events: [AppointmentConfirmed]", "events: [HomeCheckAppointmentProposed]");
        var files = SliceScaffolder.ScaffoldAll(parse(yaml)).Where(x => x.Key.EndsWith(".cs"));

        files.Count(x => x.Value.Contains("public record HomeCheckAppointmentProposed(")).ShouldBe(1);
    }
}
