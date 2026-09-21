using Bobcat.Alba;
using Bobcat;
using Bobcat.Engine;
using Bobcat.Generated.EventModel;
using Bobcat.Runtime;
using JasperFx.Events.EventModeling;

namespace BankAccountES.Tests;

/// <summary>
/// The Bobcat-side half of the multi-source event-model vehicle (bobcat#172). Assembles one
/// provenance-stamped <see cref="EventModelDescriptor"/> from the four design-time sources —
/// the running host's Wolverine/HTTP chains (Derived), the store's projection registry (Derived,
/// jasperfx#825 / bobcat#300), the C# overlay in Program.cs (Declared), and this assembly's
/// generated <c>BobcatEventModelSource</c> (Declared) — exactly the way
/// <c>EventModelDiscovery</c> would, and lets Features/EventModel.feature assert that the merge
/// attributes every role, keeps declarations where nothing outranks them, and surfaces the
/// planted disagreement (FreezeAccount.cs) as a hotspot instead of swallowing it.
/// </summary>
/// <remarks>
/// The generated source is <c>internal</c> to this assembly and the host cannot reference its own
/// spec project, so this fixture — running where both the booted host's container and the
/// generated source are reachable — is currently the only place all three sources can compose.
/// The host's own <c>event-model --url</c> export carries the chains and the overlay but not
/// these slices; that gap is part of what bobcat#172 exists to surface.
/// </remarks>
public class EventModelFixture : Fixture
{
    private IReadOnlyList<EventModelDescriptor> _models = [];

    private EventModelDescriptor Model
        => _models.Count == 1
            ? _models[0]
            : throw new SpecCriticalException(
                $"Expected exactly one assembled model, but got [{string.Join(", ", _models.Select(m => m.Name))}] — assemble first, and check every source names the same model.");

    private EventModelSliceDescriptor SliceNamed(string name)
        => Model.Slices.FirstOrDefault(s => s.Name == name)
           ?? throw new SpecAssertionException(
               $"No slice named '{name}'. Slices: {string.Join(", ", Model.Slices.Select(s => s.Name))}");

    [When("the event model is assembled from the chains, the overlay, the store and this assembly's specs")]
    public async Task AssembleEventModel()
    {
        var host = Context!.GetResource<IAlbaResource>();

        // What EventModelDiscovery.AssembleAsync(services) would do, plus this assembly's
        // generated source — which the host's container cannot see (see the class remarks).
        // DiscoverAsync finds every IEventModelDefinitionSource the host registered: Wolverine's
        // chains, the overlay, and — since JasperFx.Events 2.69 (jasperfx#825, bobcat#300) — the
        // store's projection registry, which AddMarten / AddFisher register unasked.
        var discovered = (await EventModelDiscovery.DiscoverAsync(host.RootServices, Context.Cancellation)).ToList();

        // Provenance is a default interface member, so it is only reachable through the interface.
        IEventModelDefinitionSource specSource = BobcatEventModelSource.Instance;
        var specs = await specSource.TryCreateAsync(host.RootServices, Context.Cancellation);
        if (specs is not null) discovered.Add(specs.WithProvenance(specSource.Provenance));

        _models = EventModelDiscovery.Assemble(discovered);

        // Vehicle plumbing: with BOBCAT_EVENT_MODEL_EXPORT set to a file path, write the composed
        // model as the same camelCase/PascalCase-enum JSON the console's PUT /api/event-model
        // accepts — this process is the only one that can see all three sources (see the class
        // remarks), so this file is how the merged picture reaches a viewer today.
        if (Environment.GetEnvironmentVariable("BOBCAT_EVENT_MODEL_EXPORT") is { Length: > 0 } path && _models.Count == 1)
        {
            var wire = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            };
            wire.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(_models[0], wire), Context.Cancellation);
        }
    }

    [Then("there is exactly one model, named {string}")]
    public void ThenOneModelNamed(string name)
    {
        if (_models.Count != 1 || _models[0].Name != name)
            throw new SpecAssertionException(
                $"Expected one model named '{name}', but got: [{string.Join(", ", _models.Select(m => m.Name))}]");
    }

    [Then("the {string} slice's {word} role is claimed by {word}")]
    public void ThenRoleClaimedBy(string slice, string role, string provenance)
    {
        var parsedRole = Enum.Parse<EventModelRole>(role);
        var expected = Enum.Parse<EventModelProvenance>(provenance);
        var actual = SliceNamed(slice).ProvenanceFor(parsedRole);
        if (actual != expected)
            throw new SpecAssertionException(
                $"{slice}.{role} is claimed by {actual?.ToString() ?? "nobody"}, expected {expected}.");
    }

    [Then("every claimed role on every slice names its source")]
    public void ThenEveryClaimedRoleIsAttributed()
    {
        var unattributed = new List<string>();
        foreach (var slice in Model.Slices)
        foreach (var role in Enum.GetValues<EventModelRole>())
        {
            if (slice.Claims(role) && slice.ProvenanceFor(role) is null)
                unattributed.Add($"{slice.Name}.{role}");
        }

        if (unattributed.Count > 0)
            throw new SpecAssertionException($"Roles with no source: {string.Join(", ", unattributed)}");
    }

    [Then("the {string} slice reports a source disagreement on {word}")]
    public void ThenSliceHasDisagreementOn(string slice, string role)
    {
        var parsedRole = Enum.Parse<EventModelRole>(role);
        if (disagreementsOn(slice, parsedRole).Count == 0)
            throw new SpecAssertionException(
                $"No SourceDisagreement hotspot on {slice}.{role}. Hotspots: {describeHotspots(slice)}");
    }

    [Then("that disagreement kept the {word} claim naming {string}")]
    public void ThenDisagreementKept(string provenance, string value)
    {
        var kept = soleDisagreement().WinningClaim!;
        if (kept.Provenance != Enum.Parse<EventModelProvenance>(provenance) || !kept.Value.Contains(value))
            throw new SpecAssertionException(
                $"Winning claim is {kept.Provenance} '{kept.Value}', expected {provenance} naming '{value}'.");
    }

    [Then("that disagreement dropped the {word} claim {string}")]
    public void ThenDisagreementDropped(string provenance, string value)
    {
        var lost = soleDisagreement().LosingClaim!;
        if (lost.Provenance != Enum.Parse<EventModelProvenance>(provenance) || lost.Value != value)
            throw new SpecAssertionException(
                $"Losing claim is {lost.Provenance} '{lost.Value}', expected {provenance} '{value}'.");
    }

    [Then("the {string} slice reports no source disagreement")]
    public void ThenSliceHasNoDisagreement(string slice)
    {
        var found = SliceNamed(slice).Hotspots.Where(h => h.Origin == HotspotOrigin.SourceDisagreement).ToList();
        if (found.Count > 0)
            throw new SpecAssertionException($"Unexpected disagreement(s) on {slice}: {describeHotspots(slice)}");
    }

    [Then("the {string} slice is in domain {string}")]
    public void ThenSliceInDomain(string slice, string domain)
    {
        var actual = SliceNamed(slice).Domain;
        if (actual != domain)
            throw new SpecAssertionException($"{slice}.Domain is '{actual}', expected '{domain}'.");
    }

    [Then("the {string} slice is triggered by {string}")]
    public void ThenSliceTriggeredBy(string slice, string label)
    {
        var actual = SliceNamed(slice).TriggerLabel;
        if (actual != label)
            throw new SpecAssertionException($"{slice}.TriggerLabel is '{actual}', expected '{label}'.");
    }

    /// <summary>
    /// Assert which read model a slice reads. bobcat#175.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vehicle previously asserted read models only as <c>ReadModelTypes role is claimed by
    /// Declared</c> — provenance, never identity — so a derived read model could be wrong, ugly or
    /// missing and every scenario still passed. wolverine#4182 was found by looking at the canvas,
    /// which is exactly the kind of catch a spec is supposed to make instead of a person.
    /// </para>
    /// <para>
    /// <b>The type name is <c>{word}</c>, not <c>{readmodel}</c>, deliberately.</b> A
    /// <c>{readmodel}</c> capture resolves to a <c>System.Type</c> at compile time and would stamp
    /// a ReadModelTypes role onto this feature's own slice — these scenarios assert the model, so
    /// contributing to it would make the vehicle observe itself. The whole feature is untagged and
    /// capture-free for that reason; a compile-time-checked name is not worth the self-reference.
    /// </para>
    /// </remarks>
    [Then("the {string} slice reads the {word} read model")]
    public void ThenSliceReadsReadModel(string slice, string readModel)
    {
        var names = SliceNamed(slice).ReadModelTypes.Select(t => t.Name).ToList();
        if (!names.Contains(readModel))
            throw new SpecAssertionException(
                $"{slice} reads [{string.Join(", ", names)}], expected '{readModel}'.");
    }

    /// <summary>
    /// bobcat#300. Slices merge by name, and the store-derived View slice is named after its
    /// document type — the same name AccountView.feature declares. Two slices here means the
    /// convention broke and the canvas shows two stickies for one projection.
    /// </summary>
    [Then("there is exactly one slice named {string}")]
    public void ThenExactlyOneSliceNamed(string name)
    {
        var matches = Model.Slices.Where(s => s.Name == name).ToList();
        if (matches.Count != 1)
            throw new SpecAssertionException(
                $"Expected exactly one slice named '{name}', found {matches.Count}. Slices: {string.Join(", ", Model.Slices.Select(s => s.Name))}");
    }

    [Then("the {string} slice has pattern {word}")]
    public void ThenSliceHasPattern(string slice, string pattern)
    {
        var actual = SliceNamed(slice).Pattern;
        if (actual != Enum.Parse<SlicePattern>(pattern))
            throw new SpecAssertionException($"{slice}.Pattern is {actual?.ToString() ?? "null"}, expected {pattern}.");
    }

    /// <summary>
    /// Identity, not provenance, for the same reason as the read-model step: a consumed-events
    /// list could be present, attributed, and wrong. <c>{word}</c> rather than <c>{event}</c> so
    /// the vehicle does not stamp a role on itself.
    /// </summary>
    [Then("the {string} slice consumes the {word} event")]
    public void ThenSliceConsumesEvent(string slice, string @event)
    {
        var names = SliceNamed(slice).ConsumedEvents.Select(t => t.Name).ToList();
        if (!names.Contains(@event))
            throw new SpecAssertionException(
                $"{slice} consumes [{string.Join(", ", names)}], expected '{@event}'.");
    }

    /// <summary>
    /// bobcat#297: what this assembly's specs say on their own, before the merge — the generated
    /// source read directly. A View scenario's arranged events are its ConsumedEvents; the store
    /// says the same thing from the projection's real apply set, and the merge is a union with no
    /// disagreement, which the store-rung scenario asserts separately.
    /// </summary>
    [Then("this assembly's specs alone say the {string} slice consumes the {word} event")]
    public void ThenSpecsAloneConsume(string slice, string @event)
    {
        var declared = BobcatEventModelSource.Describe().Slices.FirstOrDefault(s => s.Name == slice)
            ?? throw new SpecAssertionException($"The specs declare no slice named '{slice}'.");
        var names = declared.ConsumedEvents.Select(t => t.Name).ToList();
        if (!names.Contains(@event))
            throw new SpecAssertionException($"The specs say {slice} consumes [{string.Join(", ", names)}], expected '{@event}'.");
    }

    /// <summary>
    /// The cross-slice join computed upstream on read (jasperfx#823): nobody declares a link, it
    /// falls out of one slice's EmittedEvents meeting another's ConsumedEvents.
    /// </summary>
    [Then("there is an {word} link from the {string} slice to the {string} slice via {word}")]
    public void ThenLinkExists(string kind, string from, string to, string via)
    {
        var parsedKind = Enum.Parse<EventModelLinkKind>(kind);
        var links = Model.Links;
        if (!links.Any(l => l.Kind == parsedKind && l.FromSlice == from && l.ToSlice == to && l.Via.Name == via))
            throw new SpecAssertionException(
                $"No {kind} link {from} → {to} via {via}. Links: " +
                string.Join(" | ", links.Select(l => $"{l.Kind} {l.FromSlice} → {l.ToSlice} via {l.Via.Name}")));
    }

    [Then("the {string} slice binds the specification {string}")]
    public void ThenSliceBindsSpecification(string slice, string identity)
    {
        var specs = SliceNamed(slice).Specifications;
        if (specs.All(s => s.Identity != identity))
            throw new SpecAssertionException(
                $"{slice} binds [{string.Join(", ", specs.Select(s => s.Identity))}], expected '{identity}'.");
    }

    // The vehicle plants exactly one disagreement (FreezeAccount.cs), so "the" disagreement is
    // well-defined; a second one appearing is itself a finding this throws on.
    private HotspotDescriptor soleDisagreement()
    {
        var all = Model.Slices
            .SelectMany(s => s.Hotspots.Where(h => h.Origin == HotspotOrigin.SourceDisagreement))
            .ToList();
        return all.Count == 1
            ? all[0]
            : throw new SpecAssertionException(
                $"Expected exactly one SourceDisagreement in the whole model, found {all.Count}: " +
                string.Join(" | ", all.Select(h => h.Text)));
    }

    private List<HotspotDescriptor> disagreementsOn(string slice, EventModelRole role)
        => SliceNamed(slice).Hotspots
            .Where(h => h.Origin == HotspotOrigin.SourceDisagreement && h.Role == role)
            .ToList();

    private string describeHotspots(string slice)
        => string.Join(" | ", SliceNamed(slice).Hotspots.Select(h => $"{h.Origin}: {h.Text}"));
}
