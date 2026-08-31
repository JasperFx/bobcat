using Bobcat;
using Bobcat.Engine;
using Bobcat.Generated.EventModel;
using Bobcat.Runtime;
using JasperFx.Events.EventModeling;

namespace BankAccountES.Tests;

/// <summary>
/// The Bobcat-side half of the four-source event-model vehicle (bobcat#172). Assembles one
/// provenance-stamped <see cref="EventModelDescriptor"/> from the three design-time sources —
/// the running host's Wolverine/HTTP chains (Derived), the C# overlay in Program.cs (Declared),
/// and this assembly's generated <c>BobcatEventModelSource</c> (Declared) — exactly the way
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

    [When("the event model is assembled from the chains, the overlay and this assembly's specs")]
    public async Task AssembleEventModel()
    {
        var host = Context!.GetResource<AlbaResource<Program>>();

        // What EventModelDiscovery.AssembleAsync(services) would do, plus this assembly's
        // generated source — which the host's container cannot see (see the class remarks).
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
