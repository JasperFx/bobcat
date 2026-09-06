namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// One published message the model designates for the slice's cascade. <see cref="HandledBy"/>
/// names the slice that takes it off the bus as its command; null means the message leaves the
/// system through an outbound external edge, so no slice in this model declares its shape and
/// the publishing slice's scaffold owns the record.
/// </summary>
public sealed record CascadedMessage(string Name, string? HandledBy)
{
    public bool LeavesTheSystem => HandledBy is null;
}

/// <summary>What the model says about one slice's published messages: what to cascade, and what looks like a modeling gap.</summary>
public sealed record BusVisibilityResolution(
    IReadOnlyList<CascadedMessage> Cascaded,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The issue #218 inference: there is no <c>busVisible:</c> flag — the model itself designates
/// that a message travels the bus. A slice's <c>messages:</c> entry is bus-visible when another
/// slice declares it as its <c>command:</c> and takes it off the bus (a non-HTTP trigger), or
/// when the publishing slice marks it leaving the system with an outbound
/// <c>externalSystems:</c> edge. Everything else is a scaffold-time warning: an unhandled
/// published command is usually a modeling gap, and a published command whose only match is an
/// HTTP-triggered slice would go unhandled on the bus — routes are not queues.
/// </summary>
public static class BusVisibility
{
    public static BusVisibilityResolution Resolve(CuratedModelFile model, CuratedSlice slice)
    {
        var cascaded = new List<CascadedMessage>();
        var warnings = new List<string>();
        var leavesTheSystem = slice.ExternalSystems.Any(x => x.Direction == "Outbound");

        foreach (var message in slice.Messages)
        {
            var handler = model.Slices.FirstOrDefault(x => !ReferenceEquals(x, slice) && x.Command == message);

            if (handler is null)
            {
                if (leavesTheSystem)
                {
                    cascaded.Add(new CascadedMessage(message, HandledBy: null));
                }
                else
                {
                    warnings.Add(
                        $"slice '{slice.Name}' publishes '{message}', but no slice declares it as its command and nothing marks it leaving the system. An unhandled published command is usually a modeling gap — add the handling slice, or an outbound `externalSystems:` edge if it genuinely leaves the system.");
                }
            }
            else if (handler.Trigger?.Kind is "Http" or "Human")
            {
                warnings.Add(
                    $"slice '{slice.Name}' publishes '{message}', but slice '{handler.Name}' takes it at a route ({handler.Trigger.Kind} trigger), not off the bus — published, it would go unhandled. Give '{handler.Name}' a MessageHandler trigger if the command truly travels the bus.");
            }
            else
            {
                cascaded.Add(new CascadedMessage(message, handler.Name));
            }
        }

        return new BusVisibilityResolution(cascaded, warnings);
    }

    /// <summary>
    /// The other end of the join: the slice that publishes this slice's command over the bus,
    /// when the model says one does. Null for an HTTP-triggered slice — its command arrives by
    /// route, so another slice publishing it is that slice's warning, not this slice's trigger.
    /// </summary>
    public static string? PublishedBy(CuratedModelFile model, CuratedSlice slice)
        => slice.Command is null || slice.Trigger?.Kind is "Http" or "Human"
            ? null
            : model.Slices.FirstOrDefault(x => !ReferenceEquals(x, slice) && x.Messages.Contains(slice.Command))?.Name;

    /// <summary>Every bus-visibility warning in the model, slice by slice — the programmatic channel for a scaffolding front-end.</summary>
    public static IReadOnlyList<string> Warnings(CuratedModelFile model)
        => model.Slices.SelectMany(x => Resolve(model, x).Warnings).ToList();
}
