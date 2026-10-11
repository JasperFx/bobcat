using JasperFx.Events.EventModeling;

namespace Bobcat;

/// <summary>
/// The Event Modeling role a type plays when a projected specification calls a vocabulary method
/// (bobcat#462). Read by the generator off <see cref="EventModelRoleAttribute"/>.
/// </summary>
public enum SliceRole
{
    /// <summary>
    /// A command the specification sends. The <b>last</b> call carrying one in a test is its act, and
    /// that command is the slice's; earlier ones arranged state.
    /// </summary>
    Command,

    /// <summary>The aggregate whose stream the specification arranges, acts on or reads.</summary>
    Aggregate,

    /// <summary>An event the act emits.</summary>
    Event,

    /// <summary>
    /// An event laid down <em>before</em> the act. On a View slice it is what the projection consumes
    /// (<c>ConsumedEvents</c>); on a Command slice it is the aggregate's own history and stamps nothing
    /// (bobcat#297). It still reaches the specification's resolved types either way.
    /// </summary>
    ArrangedEvent,

    /// <summary>A read model the specification arranges or asserts on.</summary>
    ReadModel,

    /// <summary>
    /// A document the specification asserts on. It reaches the specification's resolved types only, never a
    /// slot on the slice — a document is not an aggregate, event or read model, exactly as Gherkin's
    /// <c>{document}</c> capture stamps no role.
    /// </summary>
    Document,

    /// <summary>A message the act sends, publishes or schedules.</summary>
    Message,
}

/// <summary>
/// Declares the Event Modeling role a scenario vocabulary method's argument or type argument plays
/// (bobcat#462), so a projected specification reaches the Event Model with its command, events,
/// aggregates, read models and messages — everything a <c>.feature</c> file's type captures used to
/// stamp — with nothing authored in the test itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from metadata, never by method name.</b> The generator reads this off the resolved method
/// symbol, the way it reads <c>[Given]</c>/<c>[Then]</c> off a base fixture in a referenced package. A
/// vocabulary in another repository (WolverineFx.Bobcat's <c>WolverineScenario</c>) declares its own roles,
/// and renaming one of its methods cannot silently stop the roles being stamped.
/// </para>
/// <para>
/// <b><see cref="From"/> names where the type comes from</b> — a type parameter or a value parameter of
/// the attributed method. Write it with <c>nameof</c>, which C# 11 allows for a method's own parameters
/// and type parameters in an attribute on that method, so a rename keeps the two in step:
/// </para>
/// <list type="bullet">
/// <item>a <b>type parameter</b> — <c>GivenEvents&lt;TAggregate&gt;</c> is
/// <c>[EventModelRole(SliceRole.Aggregate, nameof(TAggregate))]</c>; the role's type is the type argument
/// at the call site;</item>
/// <item>a <b>value parameter</b> — <c>WhenReceived(object message)</c> is
/// <c>[EventModelRole(SliceRole.Command, nameof(message))]</c>; the role's type is read off the argument
/// expression: <c>new T(…)</c>, <c>typeof(T)</c>, <c>Specify&lt;T&gt;()</c> at the root of a
/// <c>.With(…)</c> chain, <c>Expect.Value(…)</c>, <c>Tagged(value, …)</c>, or failing those the
/// argument's static type when it is a concrete type of the application's own. A <c>params</c>
/// parameter contributes every element.</item>
/// </list>
/// <para>
/// An argument whose type cannot be known at compile time — <c>object</c>, an interface, a table of rows,
/// a <c>null</c> — contributes nothing rather than a guess. A method may carry several roles, one
/// attribute each: <c>GivenEvents&lt;TAggregate&gt;(id, params object[] events)</c> is both an
/// <see cref="SliceRole.Aggregate"/> and an <see cref="SliceRole.ArrangedEvent"/>.
/// </para>
/// <para>
/// <b>Inherited by overrides.</b> The generator walks an override back to the declaration that carries
/// the attribute, so a derived scenario need not restate it.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class EventModelRoleAttribute : Attribute
{
    /// <param name="role">The role the type plays.</param>
    /// <param name="from">The type parameter or value parameter the type is read from — write it with <c>nameof</c>.</param>
    public EventModelRoleAttribute(SliceRole role, string from)
    {
        Role = role;
        From = from;
    }

    /// <summary>The role the type plays.</summary>
    public SliceRole Role { get; }

    /// <summary>The type parameter or value parameter of the attributed method that the type is read from.</summary>
    public string From { get; }
}

/// <summary>
/// Declares how a scenario act reaches the application (bobcat#462, bobcat#463): stamped as the slice's
/// <c>TriggerKind</c> — and, for HTTP, its route and verb — when this method is the specification's act.
/// </summary>
/// <remarks>
/// <para>
/// Read only off the call the generator takes as the act — the last one carrying a
/// <see cref="SliceRole.Command"/> — so an HTTP call that merely arranges state does not make a
/// message-driven slice an HTTP one.
/// </para>
/// <para>
/// <b>A derived fact, not a declaration.</b> Upstream's <c>TriggerKind</c> is derived by the producing
/// source and never declared through the overlay, which is why this sits on the <em>vocabulary</em> — the
/// act says how it dispatches — and not on <c>[BobcatSlice]</c>, where a test would be claiming it.
/// </para>
/// <code>
/// [EventModelRole(SliceRole.Command, nameof(command))]
/// [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "POST")]
/// public Task WhenPosted(object command, string route, Action&lt;Scenario&gt;? configure = null)
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class EventModelTriggerAttribute : Attribute
{
    public EventModelTriggerAttribute(TriggerKind kind) => Kind = kind;

    /// <summary>How the act reaches the application.</summary>
    public TriggerKind Kind { get; }

    /// <summary>
    /// The value parameter carrying the HTTP route — write it with <c>nameof</c>. Its argument must be a
    /// compile-time constant to be stamped; any other argument leaves the kind certain and the route
    /// unknown, which is better on a canvas than a wrong route.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>The HTTP verb, for an <see cref="TriggerKind.Http"/> act. <c>POST</c> when not set.</summary>
    public string? HttpMethod { get; set; }
}
