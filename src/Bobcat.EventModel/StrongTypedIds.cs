using Bobcat.EventModel.Emlang;

namespace Bobcat.EventModel;

/// <summary>
/// Which aggregates an import gives a <b>strong-typed identifier</b> (bobcat#512/#433), what it is
/// called, and what it wraps — the one decision every writer asks, so a stub's member, the
/// definition's <c>IdentifiedBy</c> and a generated specification's local cannot disagree about an
/// identity's type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Off unless asked</b> (<c>--strong-typed-ids</c>). An undeclared identity is a <see cref="Guid"/>,
/// which is what every Critter Stack store defaults a stream id to, and typing them is a modelling
/// choice the board does not express. <see cref="None"/> is the empty answer and every writer takes
/// it as "the primitive, as before".
/// </para>
/// <para>
/// <b>Per aggregate, not per type.</b> A stream is the thing that has an identity: an aggregate's own
/// <c>Id</c>, the <c>Id</c> of the command that decides against it, the <c>{Aggregate}Id</c> a command
/// against several streams carries, and the <c>Id</c> of a read model projected from it are all the
/// same identity, and Marten requires a single-stream projection's document id to BE the stream's id
/// type. Minting one per command or per read model would say otherwise.
/// </para>
/// <para>
/// <b>The name is <c>{Aggregate}Id</c>, and that is a constraint rather than a style</b>: Marten
/// considers a type a strong-typed identity only when its name ends in <c>Id</c> (case sensitive),
/// so the convention is also the requirement. It wraps what the stream's identity would otherwise
/// have been (<see cref="CSharpModelWriter.StreamIdentityType"/>) — a <see cref="Guid"/>, or a
/// <see cref="string"/> for a natural key.
/// </para>
/// <para>
/// <b>A name the model already uses is refused, not shadowed.</b> If the board names an element
/// <c>OrderId</c>, the aggregate <c>Order</c> keeps a primitive id and the import says so
/// (<see cref="ImportCalloutCodes.StrongTypedIdNameTaken"/>). Writing both would be two types of one
/// name in one namespace — output that does not build, which is the one thing a scaffolder must
/// never produce.
/// </para>
/// </remarks>
public sealed class StrongTypedIds
{
    private readonly Dictionary<string, string> _byAggregate;
    private readonly Dictionary<string, string> _inner;

    /// <summary>
    /// The identity type of one element's one member: every member the model marks as an element's
    /// identity, and every element's conventional <c>Id</c> / <c>AggregateId</c>, keyed by
    /// <c>{element}.{member}</c>. The name rule alone cannot reach these — a stream keyed by an email
    /// has an identity member called <c>Email</c>.
    /// </summary>
    private readonly Dictionary<string, string> _members;

    private StrongTypedIds(Dictionary<string, string> byAggregate, Dictionary<string, string> inner,
        Dictionary<string, string> members, IReadOnlyList<ImportCallout> callouts)
    {
        _byAggregate = byAggregate;
        _inner = inner;
        _members = members;
        Callouts = callouts;
    }

    /// <summary>No strong-typed identifiers: every identity is the primitive it always was.</summary>
    public static StrongTypedIds None { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        []);

    /// <summary>
    /// One identity type per aggregate the model names, skipping any whose name the model has
    /// already taken.
    /// </summary>
    /// <param name="model">The imported model.</param>
    /// <param name="types">
    /// Every type name the import will write, so a collision is found before anything is emitted.
    /// </param>
    public static StrongTypedIds For(ImportedEventModel model, IEnumerable<string> types)
    {
        var taken = types.Select(x => CSharpModelWriter.Identifiers.Sanitize(x ?? ""))
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var byAggregate = new Dictionary<string, string>(StringComparer.Ordinal);
        var inner = new Dictionary<string, string>(StringComparer.Ordinal);
        var callouts = new List<ImportCallout>();

        var aggregates = model.Slices
            .SelectMany(CSharpModelWriter.AggregatesOf)
            .Select(x => CSharpModelWriter.Identifiers.Sanitize(x ?? ""))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal);

        foreach (var aggregate in aggregates)
        {
            var name = aggregate + "Id";

            if (taken.Contains(name))
            {
                callouts.Add(new ImportCallout(
                    Slice: "",
                    ImportCalloutCodes.StrongTypedIdNameTaken,
                    [aggregate],
                    $"{aggregate} keeps a primitive identity: the model already names a type {name}, so a "
                    + "strong-typed id of that name would be two types of one name in one namespace.",
                    Override: null)
                {
                    Type = aggregate,
                    Fix = $"Rename the board's {name} element, or import without --strong-typed-ids."
                });
                continue;
            }

            byAggregate[aggregate] = name;
            inner[name] = CSharpModelWriter.StreamIdentityType(model, aggregate);
        }

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in CSharpModelWriter.StubNames(model))
        {
            if (CSharpModelWriter.IdentityOwnerOf(model, element) is not { } owner) continue;
            if (byAggregate.GetValueOrDefault(owner) is not { } id) continue;

            members[$"{element}.Id"] = id;
            members[$"{element}.AggregateId"] = id;

            var marked = model.Slices
                .SelectMany(s => s.Elements)
                .Where(x => CSharpModelWriter.Identifiers.Sanitize(x.Key) == element)
                .SelectMany(x => x.Value.Identities)
                .Select(x => CSharpModelWriter.Identifiers.Sanitize(EmlangImport.PascalName(x)))
                .Where(x => x.Length > 0);

            foreach (var field in marked) members[$"{element}.{field}"] = id;
        }

        return new StrongTypedIds(byAggregate, inner, members, callouts);
    }

    /// <summary>Whether any identity is typed at all — false for <see cref="None"/>.</summary>
    public bool Any => _byAggregate.Count > 0;

    /// <summary>What the import could not type, and why.</summary>
    public IReadOnlyList<ImportCallout> Callouts { get; }

    /// <summary>Every identity type to be written, in aggregate order, with what it wraps.</summary>
    public IReadOnlyList<(string Name, string Inner)> Declarations
        => _byAggregate.Values.Select(name => (name, _inner[name])).ToList();

    /// <summary>The identity type of an aggregate, or null when it keeps a primitive one.</summary>
    public string? IdOf(string aggregate)
        => _byAggregate.GetValueOrDefault(CSharpModelWriter.Identifiers.Sanitize(aggregate ?? ""));

    /// <summary>What an identity type wraps (<c>Guid</c> or <c>string</c>), or null for anything else.</summary>
    public string? Inner(string? type) => type is null ? null : _inner.GetValueOrDefault(type);

    /// <summary>Whether <paramref name="type"/> is one of these identity types.</summary>
    public bool Contains(string? type) => Inner(type) is not null;

    /// <summary>
    /// The type a member called <c>{Aggregate}Id</c> takes, or null when no aggregate of that name is
    /// typed. The rule is the member's NAME, which is how an event carrying <c>OrderId</c> gets the
    /// aggregate's identity type without the model saying anything beyond the name it already uses.
    /// </summary>
    public string? IdOfMember(string member)
    {
        if (member is not { Length: > 2 } || !member.EndsWith("Id", StringComparison.Ordinal)) return null;
        return IdOf(member[..^2]);
    }

    /// <summary>
    /// The type <paramref name="member"/> takes on <paramref name="stub"/>: the identity the model marks
    /// for that element (a stream keyed by an email has an identity member called <c>Email</c>), its
    /// conventional <c>Id</c> or <c>AggregateId</c>, or else the <c>{Aggregate}Id</c> name rule. Null
    /// when the member is not an identity, or its stream keeps a primitive one.
    /// </summary>
    public string? IdOfMember(string stub, string member)
    {
        if (!Any || member is not { Length: > 0 }) return null;

        return _members.GetValueOrDefault($"{CSharpModelWriter.Identifiers.Sanitize(stub ?? "")}.{member}")
               ?? IdOfMember(member);
    }
}
