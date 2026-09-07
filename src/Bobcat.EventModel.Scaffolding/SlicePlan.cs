namespace Bobcat.EventModel.Scaffolding;

/// <summary>The three shapes a Command or Automation slice scaffolds into.</summary>
public enum SliceShape
{
    /// <summary>The two-hop OPT-IN (#218): the endpoint mints identity and cascades, binding no write model.</summary>
    Translation,

    /// <summary>The default for an HTTP- or Human-triggered command slice: the endpoint IS the handler.</summary>
    CollapsedEndpoint,

    /// <summary>An automation, or a command taken off the bus: a message handler over the write model.</summary>
    WriteModelHandler
}

/// <summary>
/// The one decision about a slice that <b>every</b> half of the scaffolder reads: the shape its
/// code takes, the route its endpoint answers on, the write model it binds, where its trigger
/// comes from, and what the model says about its published messages.
/// </summary>
/// <param name="StartsStream">
/// This slice CREATES its stream, so it binds no write model (issue #239). Read from the one fact
/// that decides it: whether the type the act carries has a field that could identify the
/// aggregate. <c>[WriteModel]</c> resolves the stream id out of the incoming message, so a trigger
/// carrying <c>AssignmentId</c>, <c>OwnerId</c> and <c>ProposedFor</c> — but no
/// <c>AppointmentId</c> — cannot bind one at all. Wolverine says exactly that, at dispatch, before
/// the body runs: "Unable to determine an aggregate id for the parameter". Emitting
/// <c>MartenOps.StartStream</c> there is not a guess about intent; it is the only shape that can
/// work.
/// <para>
/// An unenriched model, where the act type has no fields at all, lands here too — and correctly:
/// a write model cannot be bound from an empty record either.
/// </para>
/// </param>
/// <remarks>
/// The code generator and the feature generator used to derive this separately, and they
/// disagreed (issue #231). A collapsed endpoint — no bus-visible command type anywhere — was
/// given a <c>When … is received</c> bus-dispatch step, and an automation's scenario named the
/// slice's command where its handler takes the trigger event. Both are <c>BOBCAT011</c>, and
/// BOBCAT011 is a <em>build</em> error: two disagreeing halves took the whole spec project down,
/// which is the same "one hole fails everything" property #226 had just removed from the app
/// project, reintroduced on the spec side.
///
/// Deriving the decision once is what makes the two unable to disagree again — the lesson the
/// aggregate (#222) and trigger-contract (#223) bugs already taught in the other direction, where
/// a name shared between artifacts had to be computed in one place.
/// </remarks>
public sealed record SlicePlan(
    CuratedSlice Slice,
    SliceShape Shape,
    string Command,
    string Aggregate,
    string Route,
    TriggerOrigin? Trigger,
    BusVisibilityResolution Visibility,
    bool StartsStream = false)
{
    /// <summary>True when the act is an HTTP POST rather than a bus dispatch — both endpoint shapes.</summary>
    public bool OverHttp => Shape is SliceShape.CollapsedEndpoint or SliceShape.Translation;


    /// <summary>The request record an endpoint takes as its body. Only meaningful when <see cref="OverHttp"/>.</summary>
    public string RequestType => $"{Command}Request";

    /// <summary>
    /// The type a scenario's act names, which is always the type the emitted code actually
    /// accepts: an HTTP slice takes its request record at a route, an automation takes its
    /// trigger event off the bus, and a bus-triggered command takes the command.
    /// </summary>
    public string ActType => OverHttp ? RequestType : Trigger?.Event ?? Command;

    /// <summary>
    /// The Gherkin act line, chosen by the same rule that chose the code's shape. The HTTP form
    /// is <c>HttpGrammars</c>' step (issue #210) and the bus form is <c>CritterStackFixture</c>'s;
    /// a feature that mixes slices needs the fixture that binds both, which
    /// <see cref="SliceScaffolder.ScaffoldFeatures"/> names in a comment at the top of the file.
    /// </summary>
    public string ActStep => OverHttp
        ? $"When {ActType} is posted to \"{Route}\""
        : $"When {ActType} is received";

    /// <summary>
    /// How this slice's code refuses, as Gherkin. The two forms are not interchangeable: a
    /// bus-dispatched command refuses by <em>throwing</em>, which <c>Then validation fails with
    /// …</c> catches, while a collapsed endpoint refuses with ProblemDetails and a 400 — which is
    /// what <c>Validate</c> returns in the very code this plan emitted, and which no
    /// caught-exception step can ever see. Emitting the wrong one is not a compile error, so it
    /// costs a permanently red scenario rather than a build; same disagreement, quieter bill.
    /// </summary>
    public IEnumerable<string> RefusalSteps(string reason)
    {
        if (OverHttp)
        {
            // The reason survives as a comment: the HTTP form has nowhere to assert it, and the
            // guard that produces it is a TODO in the emitted Validate stub.
            yield return $"# refused with: \"{reason}\"";
            yield return "Then the response is 400";
        }
        else
        {
            yield return $"Then validation fails with \"{reason}\"";
        }

        yield return "And no events are emitted";
    }
}

/// <summary>
/// One event a View slice's projection folds, and the Guid field a fan-out can route it by —
/// null when the model names none, which is the difference between a MultiStreamProjection that
/// registers and one that cannot (issue #232).
/// </summary>
public sealed record ViewSource(string Event, string? IdentityField);
