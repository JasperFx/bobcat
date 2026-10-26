namespace Bobcat.Ui.EventModel;

/// <summary>
/// <c>GET /api/event-models</c> (bobcat#799): every model the console holds, what the bare
/// <c>GET /api/event-model</c> serves, and which one the canvas should open on.
/// </summary>
/// <param name="Current">The model a bare read serves: the one pushed last. Null when none is held.</param>
/// <param name="Preferred">The model the canvas opens on when the URL names none — see <see cref="PreferredOf"/>.</param>
/// <param name="Models">Every model, most recently pushed first.</param>
public sealed record EventModelListing(string? Current, string? Preferred, IReadOnlyList<EventModelListingEntry> Models)
{
    /// <summary>
    /// Build the listing from what the store holds and the runs on the board.
    /// </summary>
    /// <param name="boardRepositories">
    /// Each board run's repository: its checkout path and, when the console resolved one, its
    /// <c>owner/name</c>.
    /// </param>
    public static EventModelListing Build(
        IReadOnlyList<EventModelSummary> models,
        string? current,
        IEnumerable<(string Path, string? Slug)> boardRepositories)
    {
        var board = boardRepositories.ToArray();
        var paths = board.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var slugs = board.Select(x => x.Slug).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = models
            .Select(m => new EventModelListingEntry(
                m.Name,
                m.LastPushedAt,
                m.Slices,
                m.Sources.Any(s => (s.Repository is { } repository && slugs.Contains(repository))
                                   || (s.Root is { } root && paths.Contains(root))),
                m.Sources))
            .ToArray();

        return new EventModelListing(current, PreferredOf(entries), entries);
    }

    /// <summary>
    /// The model the canvas opens on: the most recently pushed of the models with a source pushed from
    /// a repository that has runs on the board, or failing that the most recently pushed of all.
    /// </summary>
    /// <remarks>
    /// The bobcat#799 case is the reason: the last-pushed model was another repository's, a fortnight
    /// old, while every run on the board was of a model pushed after it from a different checkout.
    /// "Last pushed" is the bare read's answer and stays it; the canvas has the board beside it and can
    /// do better. A model pushed before pushes said where they came from cannot be matched, and falls to
    /// the push-time rule like any other.
    /// </remarks>
    public static string? PreferredOf(IReadOnlyList<EventModelListingEntry> models)
        => (models.Where(m => m.HasRunsOnBoard).MaxBy(m => m.LastPushedAt)
            ?? models.MaxBy(m => m.LastPushedAt))?.Name;
}

/// <summary>One model in <see cref="EventModelListing"/>.</summary>
/// <param name="HasRunsOnBoard">A source of it was pushed from a repository with runs on the board.</param>
public sealed record EventModelListingEntry(
    string Name,
    DateTimeOffset LastPushedAt,
    int Slices,
    bool HasRunsOnBoard,
    IReadOnlyList<EventModelSourceSummary> Sources);
