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
/// <param name="Aggregate">The write model the slice's handler binds. Always a name, because a
/// handler must bind something and <see cref="SliceScaffolder.ScaffoldAggregates"/> emits a type
/// for the synthesized one.</param>
/// <param name="ArrangeAggregate">The aggregate a scenario's arrange steps name — null when the
/// slice has no write model and the model identifies no stream for it (issue #240). A View slice
/// is the case: nothing emits a synthesized <c>{Slice}Model</c>, so naming one is BOBCAT011.</param>
/// <param name="StartsStream">Whether the slice STARTS its stream rather than appending to one
/// (issue #239) — see <see cref="SliceScaffolder.CreatesTheStream"/> for the two signals it takes
/// and why either alone gets it wrong.</param>
/// <param name="AggregateWarnings">What the feature says out loud when the arrange steps cannot
/// be trusted — arranged events spanning several aggregates, or none this model declares.</param>
public sealed record SlicePlan(
    CuratedSlice Slice,
    SliceShape Shape,
    string Command,
    string Aggregate,
    string? ArrangeAggregate,
    IReadOnlyList<string> AggregateWarnings,
    bool StartsStream,
    string Route,
    TriggerOrigin? Trigger,
    BusVisibilityResolution Visibility)
{
    /// <summary>True when the act is an HTTP POST rather than a bus dispatch — both endpoint shapes.</summary>
    public bool OverHttp => Shape is SliceShape.CollapsedEndpoint or SliceShape.Translation;

    /// <summary>
    /// The record an endpoint takes as its body — the command, under the name the BOARD gave it.
    /// </summary>
    /// <remarks>
    /// No <c>Request</c> suffix. The board says <c>ConfirmAppointment</c>, so the type is
    /// <c>ConfirmAppointment</c>: a slice's command is the same thing whether it arrives over HTTP
    /// or over the bus, and decorating it by transport makes the model and the code disagree about
    /// what a thing is called for no gain. <c>Response</c> keeps its suffix, because that names the
    /// other half of an exchange rather than renaming the command.
    ///
    /// It also closes half of wolverine#4385 for free: the derived Event Model names a slice after
    /// its message type, so a command named for the board matches the declared slice name and the
    /// two models merge instead of stacking up as two disconnected diagrams.
    /// </remarks>
    public string RequestType => Command;

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
