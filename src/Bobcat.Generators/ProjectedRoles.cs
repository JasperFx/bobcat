using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#462: the Event Modeling roles a projected specification states by calling scenario vocabulary —
/// <c>GivenEvents&lt;Account&gt;(id, new AccountOpened(…))</c> is an aggregate and an arranged event,
/// <c>WhenReceived(new Deposit(…))</c> the act — read off <c>[EventModelRole]</c> /
/// <c>[EventModelTrigger]</c> on the resolved method symbol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attributes from metadata, never method names.</b> The vocabulary lives in other packages and
/// other repositories (WolverineFx.Bobcat's <c>WolverineScenario</c>). Matching on names would let a
/// rename there stop the roles being stamped with no error anywhere; the attribute travels with the
/// method into metadata, so it is the same mechanism that finds a base fixture's <c>[Given]</c> steps.
/// </para>
/// <para>
/// <b>Type, never guess.</b> An argument whose type is not knowable at compile time — <c>object</c>,
/// an interface, a table of rows, a <c>null</c> — contributes nothing. A missing arrow on the canvas is
/// recoverable; a wrong one is believed. That includes a type generated code cannot name: a private,
/// protected or file-local type stamps nothing, because <c>typeof</c> of it would not compile.
/// </para>
/// <para>
/// <b>The act is a call the test makes.</b> A call inside a local function or a lambda may run once, many
/// times or never, so it can never be the act. Its roles are still read — the types a test names in a
/// <c>Should.ThrowAsync(() =&gt; …)</c> or a helper lambda are facts about what it exercises — but which
/// call is the act depends on execution order the generator cannot see.
/// </para>
/// </remarks>
internal static class ProjectedRoles
{
    internal const string RoleAttributeName = "Bobcat.EventModelRoleAttribute";
    internal const string TriggerAttributeName = "Bobcat.EventModelTriggerAttribute";

    /// <summary>
    /// <c>Bobcat.SliceRole</c> member name → the role word <see cref="EventModelEmitter"/> stamps on.
    /// Read by member name rather than by value, so the enum may grow without renumbering anything;
    /// <c>ProjectedRoleTests</c> pins every member to an entry.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RoleWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Command"] = EventModelEmitter.Command,
        ["Aggregate"] = EventModelEmitter.Aggregate,
        ["Event"] = EventModelEmitter.Event,
        ["ArrangedEvent"] = EventModelEmitter.Consumed,
        ["ReadModel"] = EventModelEmitter.ReadModel,
        ["Document"] = EventModelEmitter.Document,
        ["Message"] = EventModelEmitter.Message,
        ["Context"] = EventModelEmitter.Context,
        ["ArrangedReadModel"] = EventModelEmitter.ArrangedReadModel,
    };

    /// <summary>What one test body states, in source order.</summary>
    internal sealed class Reading
    {
        /// <summary>Every (role word, qualified type) stamped, in call order.</summary>
        public readonly List<(string Role, string Type)> Roles = new();

        /// <summary>
        /// The act's command: the last <c>Command</c> stamped (bobcat#464's rule). Null when the test's last act is a
        /// read (<see cref="ReadAct"/>) rather than a command.
        /// </summary>
        public string? Act;

        /// <summary>
        /// bobcat#501: the type the act <em>reads</em>, when the test's last act is a read — a call whose vocabulary
        /// declares an HTTP <c>GET</c>/<c>HEAD</c> trigger and a <c>ReadModel</c> role, and no command. Such a test
        /// describes a Query slice. Null when the last act is a command, or there is none.
        /// </summary>
        public string? ReadAct;

        /// <summary>The act's <c>TriggerKind</c> member name, when its method declares one.</summary>
        public string? TriggerKind;

        /// <summary>
        /// The act's route, or null when the act has no HTTP trigger, declares no route, or passes one that
        /// cannot be known at compile time (bobcat#500 resolves a route name and an interpolated route through
        /// <see cref="TriggerRoutes"/>). Never "": an unknown route is null, so it cannot beat a known one when
        /// slices fold.
        /// </summary>
        public string? TriggerRoute;

        /// <summary>
        /// True when <see cref="TriggerRoute"/> was rebuilt from an interpolated route that matched no single
        /// endpoint template (bobcat#500), so its parameter names are the test's, not the endpoint's. A read or
        /// resolved route on the same slice replaces it.
        /// </summary>
        public bool TriggerRouteReconstructed;

        /// <summary>
        /// The constant route name the act passed through <c>RouteName</c>, resolved or not (bobcat#500). Never
        /// stamped as a route; it rides <c>PublisherOrigin.HttpRouteName</c> (JasperFx 2.85.0) instead.
        /// </summary>
        public string? TriggerRouteName;

        /// <summary>
        /// Why the act's route could not be known at all — BOBCAT038 — and where, or null. Kept for the act only,
        /// like the rest of the trigger.
        /// </summary>
        public string? RouteProblem;

        public Location? RouteProblemLocation;

        /// <summary>The HTTP act's verb, when its vocabulary declares one or its matched endpoint does.</summary>
        public string? HttpMethod;

        /// <summary>Every type stamped, distinct, in call order — the specification's <c>ResolvedTypes</c>.</summary>
        public List<string> ResolvedTypes()
        {
            var resolved = new List<string>();
            foreach (var (_, type) in Roles)
                if (!resolved.Contains(type)) resolved.Add(type);
            return resolved;
        }
    }

    public static Reading Read(MethodDeclarationSyntax method, SemanticModel model, CancellationToken ct)
    {
        var reading = new Reading();
        SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (body is null) return reading;

        var compilation = model.Compilation;
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetOperation(invocation, ct) is not IInvocationOperation operation) continue;

            var (carrier, roles) = attributesOf(operation.TargetMethod, RoleAttributeName);
            if (carrier is null) continue;

            string? command = null;
            string? read = null;
            foreach (var role in roles)
            {
                if (role.ConstructorArguments.Length < 2) continue;
                if (roleWordOf(role.ConstructorArguments[0]) is not { } word) continue;
                if (role.ConstructorArguments[1].Value is not string from) continue;

                foreach (var type in typesFrom(operation, carrier, from, compilation))
                {
                    reading.Roles.Add((word, type));
                    if (word == EventModelEmitter.Command) command = type;
                    else if (word == EventModelEmitter.ReadModel) read ??= type;
                }
            }

            // The act is the LAST act call, and its trigger comes with it: an HTTP call that only arranged state
            // must not make a message-driven slice an HTTP one. A call in a local function or a lambda is not one
            // the test is known to make, or to make last: its roles count, but it never becomes the act
            if (isDeferred(invocation, body)) continue;

            if (command is not null)
            {
                reading.Act = command;
                reading.ReadAct = null;
                readTrigger(operation, reading, compilation);
            }
            else if (read is not null && isReadTrigger(operation.TargetMethod))
            {
                // bobcat#501: a read-only act — GET/HEAD plus a read role, no command. The trigger attribute is
                // what tells it from an assertion: ThenReadModel<T> carries the same role and no trigger
                reading.Act = null;
                reading.ReadAct = read;
                readTrigger(operation, reading, compilation);
            }
        }

        return reading;
    }

    /// <summary>A BOBCAT034 finding at the attribute that names nothing.</summary>
    internal sealed class Finding
    {
        public Location? Location;
        public string Message = "";
    }

    /// <summary>
    /// Whether a method declaration could carry a role or trigger attribute — a syntactic cut, so the
    /// BOBCAT034 provider binds only the methods that name one rather than every attributed method.
    /// An attribute applied through a <c>using</c> alias is missed; the alias would be the odd one out.
    /// </summary>
    public static bool IsDeclarationCandidate(SyntaxNode node)
        => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 } method
           && method.AttributeLists.Any(list => list.Attributes.Any(attribute =>
           {
               var name = attribute.Name.ToString();
               return name.Contains("EventModelRole") || name.Contains("EventModelTrigger");
           }));

    /// <summary>
    /// BOBCAT034: every <c>From</c> / <c>Route</c> / <c>RouteName</c> on this method's role attributes names one of its
    /// parameters (or, for <c>From</c>, its type parameters or a type parameter of a type declaring it).
    /// Null when there is nothing to report.
    /// </summary>
    public static List<Finding>? CheckDeclaration(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not IMethodSymbol method) return null;

        // AggregateScenario<TAggregate> { [EventModelRole(SliceRole.Aggregate, nameof(TAggregate))] … }:
        // the type argument a closed scenario base supplies is as knowable at the call as a method's own
        var typeParameters = method.TypeParameters.Select(x => x.Name).ToList();
        for (var type = method.ContainingType; type is not null; type = type.ContainingType)
            typeParameters.AddRange(type.TypeParameters.Select(x => x.Name));

        List<Finding>? findings = null;
        foreach (var attribute in method.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            var checks = new List<(string? Named, string Kind, bool TypeParametersCount)>();
            if (name == RoleAttributeName)
            {
                checks.Add((attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string : null, "From", true));
            }
            else if (name == TriggerAttributeName)
            {
                // Route and, since bobcat#500, RouteName: each names a value parameter when it is set at all
                foreach (var argument in new[] { "Route", "RouteName" })
                {
                    if (attribute.NamedArguments.FirstOrDefault(x => x.Key == argument).Value.Value is string value)
                        checks.Add((value, argument, false));
                }
            }
            else continue;

            foreach (var (named, kind, typeParametersCount) in checks)
            {
                if (named is not null
                    && (method.Parameters.Any(x => x.Name == named)
                        || (typeParametersCount && typeParameters.Contains(named))))
                    continue;

                var candidates = method.Parameters.Select(x => x.Name)
                    .Concat(typeParametersCount ? typeParameters : Enumerable.Empty<string>())
                    .ToList();

                (findings ??= new List<Finding>()).Add(new Finding
                {
                    Location = attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation(),
                    Message = $"{kind} = \"{named}\" on '{method.Name}' names no "
                              + (typeParametersCount ? "parameter or type parameter" : "parameter")
                              + $" of it, so the role is never stamped at any call. "
                              + (candidates.Count == 0
                                  ? "The method has none to name."
                                  : $"It has: {string.Join(", ", candidates)}.")
                              + $" Write it with nameof(…) so a rename keeps the two in step."
                });
            }
        }

        return findings;
    }

    /// <summary>
    /// bobcat#501: whether the method declares a read-only HTTP trigger — <c>[EventModelTrigger(TriggerKind.Http,
    /// HttpMethod = "GET")]</c> or <c>"HEAD"</c>. An unset verb is POST, so it is not a read.
    /// </summary>
    private static bool isReadTrigger(IMethodSymbol method)
    {
        var trigger = attributesOf(method, TriggerAttributeName).Attributes.FirstOrDefault();
        if (trigger is null || trigger.ConstructorArguments.Length == 0) return false;
        if (enumMemberName(trigger.ConstructorArguments[0]) != "Http") return false;

        var verb = trigger.NamedArguments.FirstOrDefault(x => x.Key == "HttpMethod").Value.Value as string;
        return string.Equals(verb, "GET", StringComparison.OrdinalIgnoreCase)
               || string.Equals(verb, "HEAD", StringComparison.OrdinalIgnoreCase);
    }

    private static void readTrigger(IInvocationOperation operation, Reading reading, Compilation compilation)
    {
        reading.TriggerKind = null;
        reading.TriggerRoute = null;
        reading.TriggerRouteReconstructed = false;
        reading.TriggerRouteName = null;
        reading.RouteProblem = null;
        reading.RouteProblemLocation = null;
        reading.HttpMethod = null;

        var trigger = attributesOf(operation.TargetMethod, TriggerAttributeName).Attributes.FirstOrDefault();
        if (trigger is null || trigger.ConstructorArguments.Length == 0) return;

        reading.TriggerKind = enumMemberName(trigger.ConstructorArguments[0]);
        if (reading.TriggerKind is null) return;

        // Only an HTTP act has a route and a verb
        if (reading.TriggerKind != "Http") return;

        string? named(string name) => trigger.NamedArguments.FirstOrDefault(x => x.Key == name).Value.Value as string;

        reading.HttpMethod = named("HttpMethod");
        var routeParameter = named("Route");
        var routeNameParameter = named("RouteName");
        if (routeParameter is null && routeNameParameter is null) return;

        // bobcat#500: a constant route, a route name an endpoint attribute declares, or an interpolated route
        // matched against the endpoints (or rebuilt from the interpolation). A route that cannot be known is
        // null, never "", so it cannot shadow a known one — and BOBCAT038 says so rather than leaving it silent.
        var resolved = TriggerRoutes.Resolve(operation, routeParameter, routeNameParameter, reading.HttpMethod, compilation);
        reading.TriggerRoute = string.IsNullOrEmpty(resolved.Route) ? null : resolved.Route;
        reading.TriggerRouteReconstructed = reading.TriggerRoute is not null && resolved.Reconstructed;
        reading.TriggerRouteName = resolved.RouteName;
        reading.HttpMethod ??= resolved.Verb;
        reading.RouteProblem = resolved.Problem;
        reading.RouteProblemLocation = resolved.ProblemLocation;
    }

    /// <summary>Whether the call sits in a local function or a lambda / anonymous method within the body.</summary>
    private static bool isDeferred(SyntaxNode invocation, SyntaxNode body)
    {
        for (var node = invocation.Parent; node is not null && node != body; node = node.Parent)
            if (node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                return true;

        return false;
    }

    /// <summary>
    /// The attributes of <paramref name="fullName"/> on this method, walking an extension method back to
    /// its definition and an override back to the declaration that carries them — and that declaration,
    /// as seen from the call (so its containing type is the constructed base a closed scenario derives
    /// from). A null carrier when no declaration carries any.
    /// </summary>
    private static (IMethodSymbol? Carrier, List<AttributeData> Attributes) attributesOf(IMethodSymbol method, string fullName)
    {
        for (var current = method.ReducedFrom ?? method; current is not null; current = current.OverriddenMethod)
        {
            var found = current.OriginalDefinition.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == fullName)
                .ToList();
            if (found.Count > 0) return (current, found);
        }

        return (null, new List<AttributeData>());
    }

    private static string? roleWordOf(TypedConstant constant)
        => enumMemberName(constant) is { } name && RoleWords.TryGetValue(name, out var word) ? word : null;

    private static string? enumMemberName(TypedConstant constant)
    {
        if (constant.Type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType) return null;

        return enumType.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, constant.Value))?.Name;
    }

    /// <summary>
    /// The types <paramref name="from"/> names at this call: a type argument of the method, an argument's
    /// type(s), or the type argument a constructed declaring type supplies for one of its type parameters.
    /// </summary>
    private static IEnumerable<string> typesFrom(IInvocationOperation operation, IMethodSymbol carrier, string from, Compilation compilation)
    {
        var definition = operation.TargetMethod.OriginalDefinition;
        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            if (definition.TypeParameters[i].Name != from) continue;
            if (i < operation.TargetMethod.TypeArguments.Length && qualifiedOrNull(operation.TargetMethod.TypeArguments[i], compilation) is { } type)
                yield return type;
            yield break;
        }

        foreach (var argument in operation.Arguments)
        {
            if (argument.Parameter?.Name != from) continue;

            // An omitted optional argument is the parameter's default, which names nothing
            if (argument.ArgumentKind == ArgumentKind.DefaultValue) yield break;

            foreach (var type in typesOf(argument.Value, compilation)) yield return type;
            yield break;
        }

        // class AggregateScenario<TAggregate> called through MyScenario : AggregateScenario<Account>: the
        // carrier's containing type is the constructed base, so TAggregate is Account at this call. Called
        // from inside the open generic itself, the argument is still a type parameter and names nothing.
        for (var type = carrier.ContainingType; type is not null; type = type.ContainingType)
        {
            var parameters = type.OriginalDefinition.TypeParameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name != from) continue;
                if (i < type.TypeArguments.Length && qualifiedOrNull(type.TypeArguments[i], compilation) is { } supplied)
                    yield return supplied;
                yield break;
            }
        }
    }

    /// <summary>The application types one argument value names, unwrapping Bobcat's own wrappers.</summary>
    private static IEnumerable<string> typesOf(IOperation? value, Compilation compilation)
    {
        while (value is IConversionOperation conversion) value = conversion.Operand;
        while (value is IParenthesizedOperation parenthesized) value = parenthesized.Operand;
        if (value is null) yield break;

        switch (value)
        {
            case ITypeOfOperation typeOf:
                if (qualifiedOrNull(typeOf.TypeOperand, compilation) is { } operand) yield return operand;
                yield break;

            // params, an explicit array, or a collection expression: every element is a value
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var element in initializer.ElementValues)
                foreach (var type in typesOf(element, compilation))
                    yield return type;
                yield break;

            case ICollectionExpressionOperation collection:
                foreach (var element in collection.Elements)
                foreach (var type in typesOf(element is ISpreadOperation spread ? spread.Operand : element, compilation))
                    yield return type;
                yield break;
        }

        if (value.Type is not INamedTypeSymbol named)
        {
            // An array variable of a concrete type: its element type is the role
            if (value.Type is IArrayTypeSymbol { ElementType: var element } && qualifiedOrNull(element, compilation) is { } elementType)
                yield return elementType;
            yield break;
        }

        if (isBobcatType(named, "Specified`1") || isBobcatType(named, "ExpectedValue`1"))
        {
            // Specify<T>()…With(…) and Expect.Value(x): the type the partial object or expectation is about
            if (qualifiedOrNull(named.TypeArguments[0], compilation) is { } specified) yield return specified;
            yield break;
        }

        if (isBobcatType(named, "TaggedValue"))
        {
            // Tagged(new SeatReserved(…), tags): the tagged value is the first argument
            if (value is IInvocationOperation { Arguments.Length: > 0 } tagged)
            {
                var first = tagged.Arguments.FirstOrDefault(x => x.Parameter?.Ordinal == 0) ?? tagged.Arguments[0];
                foreach (var type in typesOf(first.Value, compilation)) yield return type;
            }

            yield break;
        }

        if (qualifiedOrNull(named, compilation) is { } concrete) yield return concrete;
    }

    private static bool isBobcatType(INamedTypeSymbol type, string metadataName)
        => type.OriginalDefinition.MetadataName == metadataName
           && type.ContainingNamespace?.ToDisplayString() == "Bobcat";

    /// <summary>
    /// The <c>global::</c>-free qualified name of a type an Event Model element can be: a concrete class,
    /// struct or record of the application's own. Never <c>object</c>, a primitive, <c>System.Type</c>, an
    /// interface, a type parameter or an unresolved type — none of those is a fact about the slice. Never a
    /// type the generated code could not name either (see <see cref="IsNameable"/>).
    /// </summary>
    private static string? qualifiedOrNull(ITypeSymbol type, Compilation compilation)
    {
        if (type is not INamedTypeSymbol named) return null;
        if (!IsNameable(named, compilation)) return null;
        if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return null;
        if (named.SpecialType != SpecialType.None || named.IsAnonymousType || named.IsTupleType) return null;
        if (named.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)))
            return null;
        if (named.ContainingNamespace?.ToDisplayString() == "Bobcat") return null;

        // global::-qualified: generated code lives in its own namespace
        return named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// Whether code the generator emits — top-level classes in its own namespace of this assembly — can
    /// write <c>typeof(…)</c> of this type. Every type along the way must be public, internal or protected
    /// internal, not file-local, accessible from this assembly as a whole (an internal type of another assembly
    /// is not), and so must every type argument. A private record nested in the test class fails: the
    /// test can name it, generated code cannot, and <c>typeof</c> of it is CS0122.
    /// </summary>
    private static bool isReachable(Accessibility accessibility)
        => accessibility is Accessibility.Public or Accessibility.Internal
            or Accessibility.ProtectedOrInternal;

    internal static bool IsNameable(ITypeSymbol type, Compilation compilation)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsNameable(array.ElementType, compilation);

            case INamedTypeSymbol named when named.TypeKind != TypeKind.Error:
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (current.IsFileLocal || !isReachable(current.DeclaredAccessibility)) return false;
                    if (current.TypeArguments.Any(x => !IsNameable(x, compilation))) return false;
                }

                return compilation.IsSymbolAccessibleWithin(named, compilation.Assembly);

            default:
                // A type parameter, a pointer, dynamic, an error type: nothing generated code can rely on
                return false;
        }
    }
}
