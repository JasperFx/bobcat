using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Bobcat.Generators;

/// <summary>
/// bobcat#500: the route an HTTP act's <c>[EventModelTrigger]</c> reaches the slice with — a constant route,
/// a <b>route name</b> resolved against the endpoint attributes the compilation can see, or an
/// <b>interpolated</b> route matched against those same endpoints and, failing a unique match, rebuilt as a
/// template from the interpolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Endpoints by attribute shape, never by package.</b> Bobcat takes no dependency on Wolverine.Http or ASP.NET
/// Core MVC. An endpoint is a method carrying an attribute whose type is, or derives from, one of the metadata
/// names below, with a constant template as its first constructor argument: Wolverine's
/// <c>[WolverinePost("/api/accounts/{accountId}/deposits", RouteName = "…")]</c> family (a class-level
/// <c>[RoutePrefix]</c> is applied the way Wolverine applies it), and MVC's <c>[HttpPost("…", Name = "…")]</c>
/// family and <c>[Route("…", Name = "…")]</c>, combined with the controller's own <c>[Route]</c> prefix and its
/// <c>[controller]</c> / <c>[action]</c> tokens. A minimal API's <c>.WithName("…")</c> is a call, not an
/// attribute, and is resolvable only at run time; so are Wolverine's namespace and global route prefixes, which
/// are configuration.
/// </para>
/// <para>
/// <b>Referenced assemblies are read from metadata</b>, but only those that reference <c>Wolverine.Http</c> or
/// <c>Microsoft.AspNetCore.Mvc.Core</c> — the assemblies that can declare such an endpoint at all — so a suite
/// does not pay to walk the whole framework. The index is built once per compilation and shared.
/// </para>
/// <para>
/// <b>A wrong route is worse than none.</b> An interpolated route takes an endpoint's template only when exactly
/// one template has the same literal segments with a parameter wherever the interpolation has a hole (and a
/// verb that agrees, where both are known). Otherwise the template is rebuilt from the interpolation and marked
/// <em>reconstructed</em>, which a constant or resolved route on the same slice replaces whatever order the
/// specifications fold in.
/// </para>
/// </remarks>
internal static class TriggerRoutes
{
    internal const string WolverineRouteAttribute = "Wolverine.Http.WolverineHttpMethodAttribute";
    internal const string WolverineRoutePrefixAttribute = "Wolverine.Http.RoutePrefixAttribute";
    internal const string MvcHttpMethodAttribute = "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";
    internal const string MvcRouteAttribute = "Microsoft.AspNetCore.Mvc.RouteAttribute";

    /// <summary>The assemblies an endpoint attribute can come from; a reference to neither declares none.</summary>
    private static readonly string[] EndpointAssemblies = { "Wolverine.Http", "Microsoft.AspNetCore.Mvc.Core" };

    /// <summary>One endpoint attribute with a constant template.</summary>
    internal sealed class Endpoint
    {
        public string Template = "";
        public string? Verb;
        public string? RouteName;
        public string Shape = "";

        /// <summary>
        /// bobcat#613: the <c>global::</c>-qualified types of the endpoint method's parameters that could be its
        /// request body — every parameter not bound from services, the route, the query string or a header.
        /// </summary>
        public List<string> BodyTypes = new();
    }

    /// <summary>What an HTTP act's route arguments resolve to.</summary>
    internal sealed class Resolution
    {
        /// <summary>The route to stamp, or null when none could be known.</summary>
        public string? Route;

        /// <summary>True when <see cref="Route"/> was rebuilt from an interpolation rather than read or matched.</summary>
        public bool Reconstructed;

        /// <summary>The verb the matched endpoint declares, when the act declared none.</summary>
        public string? Verb;

        /// <summary>The constant route name the act passed, resolved or not.</summary>
        public string? RouteName;

        /// <summary>Why no route could be known — the BOBCAT038 message — or null.</summary>
        public string? Problem;

        public Location? ProblemLocation;
    }

    private static readonly ConditionalWeakTable<Compilation, List<Endpoint>> indexes = new();

    /// <summary>Every endpoint attribute with a constant template in the compilation and the references that can declare one.</summary>
    internal static List<Endpoint> EndpointsOf(Compilation compilation)
        => indexes.GetValue(compilation, build);

    /// <summary>
    /// Resolve the act's route from its <c>Route</c> and <c>RouteName</c> arguments. <paramref name="routeParameter"/>
    /// and <paramref name="routeNameParameter"/> are the parameter names the trigger attribute gives, either null.
    /// </summary>
    public static Resolution Resolve(
        IInvocationOperation operation, string? routeParameter, string? routeNameParameter, string? declaredVerb, Compilation compilation)
    {
        var resolution = new Resolution();
        var method = operation.TargetMethod.Name;
        var problems = new List<string>();
        Location? where = null;
        string? reconstructed = null;

        var route = argumentFor(operation, routeParameter);
        if (route is not null)
        {
            if (constantString(route) is { } constant)
            {
                if (constant.Length > 0)
                {
                    resolution.Route = constant;
                    return resolution;
                }

                problems.Add($"its '{routeParameter}' argument is empty");
            }
            else if (isNullConstant(route))
            {
                problems.Add($"its '{routeParameter}' argument is null");
            }
            else if (shapeOf(route) is { } parts)
            {
                // $"/api/{Version}/accounts" with a const Version: no hole left, so it is a constant after all
                if (parts.All(x => x.Literal is not null))
                {
                    resolution.Route = string.Concat(parts.Select(x => x.Literal));
                    return resolution;
                }

                var matched = matchEndpoint(parts, declaredVerb, compilation);
                if (matched.Template is not null)
                {
                    resolution.Route = matched.Template;
                    resolution.Verb = matched.Verb;
                    return resolution;
                }

                // Held back: a route name that resolves is the endpoint's own template, and beats a guess
                reconstructed = reconstruct(parts);
            }
            else
            {
                problems.Add($"its '{routeParameter}' argument is neither a constant nor an interpolated string, so the route is only known at run time");
            }

            if (reconstructed is null) where ??= route.Syntax.GetLocation();
        }

        var routeName = argumentFor(operation, routeNameParameter);
        if (routeName is not null)
        {
            if (constantString(routeName) is { Length: > 0 } name)
            {
                resolution.RouteName = name;
                var endpoints = EndpointsOf(compilation).Where(x => x.RouteName == name).ToList();
                var templates = endpoints.Select(x => x.Template).Distinct(StringComparer.Ordinal).ToList();
                if (templates.Count == 1)
                {
                    resolution.Route = templates[0];
                    resolution.Verb = declaredVerb is null ? singleVerb(endpoints) : null;
                    return resolution;
                }

                problems.Add(templates.Count == 0
                    ? $"the route name \"{name}\" is on no endpoint attribute in this compilation or its references "
                      + "([WolverineGet/Post/…(…, RouteName = …)], [HttpGet/Post/…(…, Name = …)] or [Route(…, Name = …)]); "
                      + "a minimal API's .WithName(…) can only be resolved at run time"
                    : $"the route name \"{name}\" is on {templates.Count} endpoints with different templates "
                      + $"({string.Join(", ", templates)})");
            }
            else if (constantString(routeName) is not null || isNullConstant(routeName))
            {
                problems.Add($"its '{routeNameParameter}' argument is empty");
            }
            else
            {
                problems.Add($"its '{routeNameParameter}' argument is not a constant, so the route name is only known at run time");
            }

            where ??= routeName.Syntax.GetLocation();
        }

        if (reconstructed is not null)
        {
            resolution.Route = reconstructed;
            resolution.Reconstructed = true;
            return resolution;
        }

        if (routeParameter is null && routeNameParameter is null) return resolution;

        if (problems.Count == 0)
            problems.Add("it was passed no route" + (routeNameParameter is null ? "" : " and no route name"));

        resolution.Problem =
            $"'{method}' is this specification's HTTP act, but {string.Join("; and ", problems)}. "
            + "The slice keeps its trigger kind with no route. Pass the endpoint's route template as a constant "
            + "(\"/api/accounts/{accountId}/deposits\", with the values after it), an interpolated string, or a route "
            + "name an endpoint attribute declares.";
        resolution.ProblemLocation = where ?? operation.Syntax.GetLocation();
        return resolution;
    }

    /// <summary>
    /// bobcat#613: an HTTP act that takes its command and no route at all (<c>WhenPosted(cmd)</c>,
    /// <c>WhenPut(cmd)</c>), where the vocabulary fills the route from the command at run time. The route is the
    /// template of the one endpoint whose request body is that command type, and the verb that endpoint's, unless
    /// the act declares its own. None, or several with different templates, is a problem rather than a guess.
    /// </summary>
    public static Resolution ResolveByCommand(
        IInvocationOperation operation, string commandType, string? declaredVerb, Compilation compilation)
    {
        var resolution = new Resolution();
        var taking = EndpointsOf(compilation).Where(x => x.BodyTypes.Contains(commandType)).ToList();
        var matches = taking
            .Where(x => declaredVerb is null || x.Verb is null || string.Equals(x.Verb, declaredVerb, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var templates = matches.Select(x => x.Template).Distinct(StringComparer.Ordinal).ToList();
        if (templates.Count == 1)
        {
            resolution.Route = templates[0];
            resolution.Verb = declaredVerb is null ? singleVerb(matches) : null;
            return resolution;
        }

        var command = commandType.StartsWith("global::", StringComparison.Ordinal) ? commandType.Substring("global::".Length) : commandType;
        var why = templates.Count > 1
            ? $"{templates.Count} endpoints with different templates take '{command}' ({string.Join(", ", templates)})"
            : taking.Count > 0
                ? $"no {declaredVerb} endpoint takes '{command}' (only {string.Join(", ", taking.Select(x => $"{x.Verb ?? "?"} {x.Template}").Distinct())})"
                : $"no endpoint attribute in this compilation or its references takes '{command}' as its request body "
                  + "([WolverinePost/Put/…(…)] or [HttpPost/Put/…(…)]); a minimal API's MapPost(…) can only be resolved at run time";

        resolution.Problem =
            $"'{operation.TargetMethod.Name}' is this specification's HTTP act and passes no route, so the route is "
            + $"read off the endpoint that takes its command, but {why}. The slice keeps its trigger kind with no route. "
            + "Pass the endpoint's route template as a constant (\"/api/accounts/{accountId}/deposits\", with the values "
            + "after it), an interpolated string, or a route name an endpoint attribute declares.";
        resolution.ProblemLocation = operation.Syntax.GetLocation();
        return resolution;
    }

    // ------------------------------------------------------------------ arguments

    private static IOperation? argumentFor(IInvocationOperation operation, string? parameter)
    {
        if (parameter is null) return null;
        var argument = operation.Arguments.FirstOrDefault(x => x.Parameter?.Name == parameter);

        // An omitted optional argument says nothing, so it is not passed at all
        if (argument is null || argument.ArgumentKind == ArgumentKind.DefaultValue) return null;
        return unwrap(argument.Value);
    }

    private static IOperation unwrap(IOperation value)
    {
        while (true)
        {
            switch (value)
            {
                case IConversionOperation conversion: value = conversion.Operand; continue;
                case IParenthesizedOperation parenthesized: value = parenthesized.Operand; continue;
                default: return value;
            }
        }
    }

    private static string? constantString(IOperation value)
        => value.ConstantValue is { HasValue: true, Value: string text } ? text : null;

    private static bool isNullConstant(IOperation value)
        => value.ConstantValue is { HasValue: true, Value: null };

    // ------------------------------------------------------------------ shapes

    /// <summary>A literal run of route text, or a hole named for the value that fills it.</summary>
    private readonly struct Part
    {
        public Part(string? literal, string? hole)
        {
            Literal = literal;
            Hole = hole;
        }

        public string? Literal { get; }
        public string? Hole { get; }
    }

    /// <summary>
    /// <c>$"/api/accounts/{id}/deposits"</c> or <c>"/api/accounts/" + id + "/deposits"</c> as literal runs and
    /// holes, with any query string dropped. Null for anything else.
    /// </summary>
    private static List<Part>? shapeOf(IOperation value)
    {
        var parts = new List<Part>();
        switch (value)
        {
            case IInterpolatedStringOperation interpolated:
                foreach (var content in interpolated.Parts)
                {
                    switch (content)
                    {
                        case IInterpolatedStringTextOperation text:
                            parts.Add(new Part(constantText(text.Text) ?? "", null));
                            break;
                        case IInterpolationOperation hole:
                            parts.Add(constantText(hole.Expression) is { } folded
                                ? new Part(folded, null)
                                : new Part(null, holeName(hole.Expression)));
                            break;
                        default:
                            return null;
                    }
                }
                break;

            case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } when value.Type?.SpecialType == SpecialType.System_String:
                if (!flatten(value, parts)) return null;
                break;

            default:
                return null;
        }

        return withoutQuery(parts);
    }

    private static bool flatten(IOperation value, List<Part> parts)
    {
        value = unwrap(value);
        if (value is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } add && value.Type?.SpecialType == SpecialType.System_String)
            return flatten(add.LeftOperand, parts) && flatten(add.RightOperand, parts);

        if (value is IInterpolatedStringOperation && shapeOf(value) is { } nested)
        {
            parts.AddRange(nested);
            return true;
        }

        parts.Add(constantText(value) is { } text ? new Part(text, null) : new Part(null, holeName(value)));
        return true;
    }

    private static string? constantText(IOperation value)
        => value.ConstantValue is { HasValue: true, Value: { } constant }
            ? Convert.ToString(constant, CultureInfo.InvariantCulture)
            : null;

    private static List<Part> withoutQuery(List<Part> parts)
    {
        var kept = new List<Part>();
        foreach (var part in parts)
        {
            if (part.Literal is { } literal && literal.IndexOf('?') is var at and >= 0)
            {
                if (at > 0) kept.Add(new Part(literal.Substring(0, at), null));
                break;
            }

            kept.Add(part);
        }

        return kept;
    }

    /// <summary>A simple identifier is its own name, a member access its last member; anything else is <c>value</c>.</summary>
    private static string holeName(IOperation expression)
    {
        expression = unwrap(expression);
        return expression switch
        {
            ILocalReferenceOperation local => local.Local.Name,
            IParameterReferenceOperation parameter => parameter.Parameter.Name,
            IFieldReferenceOperation field => field.Field.Name,
            IPropertyReferenceOperation property when !property.Property.IsIndexer => property.Property.Name,
            _ => "value"
        };
    }

    /// <summary>The literal runs with each hole as <c>{name}</c>; a repeated name is numbered so the template stays valid.</summary>
    private static string reconstruct(List<Part> parts)
    {
        var sb = new StringBuilder();
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (part.Literal is { } literal)
            {
                sb.Append(literal);
                continue;
            }

            var name = part.Hole!;
            if (used.TryGetValue(name, out var count))
            {
                used[name] = count + 1;
                name += (count + 1).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                used[name] = 1;
            }

            sb.Append('{').Append(name).Append('}');
        }

        return sb.ToString();
    }

    private const char HoleMark = '\u0001';

    /// <summary>
    /// The comparable shape: literal text lower-cased, every hole or route parameter one mark, no leading or
    /// trailing slash. <c>/api/accounts/{id:guid}/deposits</c> and <c>$"/api/accounts/{theAccount}/deposits"</c>
    /// share one.
    /// </summary>
    private static string shapeKey(List<Part> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Literal is { } literal) sb.Append(literal.ToLowerInvariant());
            else sb.Append(HoleMark);
        }

        return sb.ToString().Trim('/');
    }

    /// <summary>A route template as parts: <c>{name}</c>, <c>{name:constraint}</c>, <c>{name?}</c>, <c>{*rest}</c> are holes.</summary>
    internal static string TemplateShape(string template)
    {
        var parts = new List<Part>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{') { literal.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}') { literal.Append('}'); i++; continue; }
            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var close = template.IndexOf('}', i);
            if (close < 0)
            {
                literal.Append(template.Substring(i));
                break;
            }

            if (literal.Length > 0) parts.Add(new Part(literal.ToString(), null));
            literal.Clear();
            parts.Add(new Part(null, template.Substring(i + 1, close - i - 1)));
            i = close;
        }

        if (literal.Length > 0) parts.Add(new Part(literal.ToString(), null));
        return shapeKey(parts);
    }

    private static (string? Template, string? Verb) matchEndpoint(List<Part> parts, string? declaredVerb, Compilation compilation)
    {
        var key = shapeKey(parts);
        var matches = EndpointsOf(compilation)
            .Where(x => x.Shape == key)
            .Where(x => declaredVerb is null || x.Verb is null || string.Equals(x.Verb, declaredVerb, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var templates = matches.Select(x => x.Template).Distinct(StringComparer.Ordinal).ToList();
        if (templates.Count != 1) return (null, null);

        return (templates[0], declaredVerb is null ? singleVerb(matches) : null);
    }

    private static string? singleVerb(List<Endpoint> endpoints)
    {
        var verbs = endpoints.Select(x => x.Verb).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return verbs.Count == 1 ? verbs[0] : null;
    }

    // ------------------------------------------------------------------ the endpoint index

    private static List<Endpoint> build(Compilation compilation)
    {
        var endpoints = new List<Endpoint>();
        visit(compilation.Assembly.GlobalNamespace, endpoints);

        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (!mayDeclareEndpoints(assembly)) continue;
            visit(assembly.GlobalNamespace, endpoints);
        }

        return endpoints;
    }

    private static bool mayDeclareEndpoints(IAssemblySymbol assembly)
        => assembly.Modules.Any(module => module.ReferencedAssemblies.Any(id => EndpointAssemblies.Contains(id.Name)));

    private static void visit(INamespaceSymbol ns, List<Endpoint> endpoints)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child) visit(child, endpoints);
            else if (member is INamedTypeSymbol type) visit(type, endpoints);
        }
    }

    private static void visit(INamedTypeSymbol type, List<Endpoint> endpoints)
    {
        foreach (var nested in type.GetTypeMembers()) visit(nested, endpoints);
        if (type.TypeKind != TypeKind.Class) return;

        List<string>? mvcPrefixes = null;
        string? wolverinePrefix = null;
        var prefixesRead = false;

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary) continue;

            foreach (var attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass is not { } attributeType) continue;

                var wolverine = derivesFrom(attributeType, WolverineRouteAttribute);
                var mvcVerb = !wolverine && derivesFrom(attributeType, MvcHttpMethodAttribute);
                var mvcRoute = !wolverine && !mvcVerb && attributeType.ToDisplayString() == MvcRouteAttribute;
                if (!wolverine && !mvcVerb && !mvcRoute) continue;

                if (!prefixesRead)
                {
                    (wolverinePrefix, mvcPrefixes) = prefixesOf(type);
                    prefixesRead = true;
                }

                var template = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Kind == TypedConstantKind.Primitive
                    ? attribute.ConstructorArguments[0].Value as string
                    : null;

                if (wolverine)
                {
                    if (template is null) continue;
                    add(endpoints, wolverineTemplate(wolverinePrefix, template), verbOf(attributeType.Name, "Wolverine"),
                        namedString(attribute, "RouteName"), method);
                }
                else
                {
                    var verb = mvcVerb ? verbOf(attributeType.Name, "Http") : null;
                    var name = namedString(attribute, "Name");
                    foreach (var combined in mvcTemplates(mvcPrefixes!, template, type, method))
                        add(endpoints, combined, verb, name, method);
                }
            }
        }
    }

    private static void add(List<Endpoint> endpoints, string template, string? verb, string? routeName, IMethodSymbol method)
        => endpoints.Add(new Endpoint
        {
            Template = template,
            Verb = verb,
            RouteName = string.IsNullOrEmpty(routeName) ? null : routeName,
            Shape = TemplateShape(template),
            BodyTypes = bodyTypesOf(method)
        });

    /// <summary>Binding attributes that say a parameter is not the request body, by simple name.</summary>
    private static readonly string[] NotBodyAttributes =
    {
        "FromServicesAttribute", "FromKeyedServicesAttribute", "FromRouteAttribute", "FromQueryAttribute",
        "FromHeaderAttribute", "FromFormAttribute", "NotBodyAttribute"
    };

    private static List<string> bodyTypesOf(IMethodSymbol method)
        => method.Parameters
            .Where(p => p.Type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, SpecialType: SpecialType.None })
            .Where(p => !p.GetAttributes().Any(a => a.AttributeClass is { } type && NotBodyAttributes.Contains(type.Name)))
            .Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static (string? Wolverine, List<string> Mvc) prefixesOf(INamedTypeSymbol type)
    {
        string? wolverine = null;
        var mvc = new List<string>();

        // [Route] is inherited (an ApiControllerBase carrying "api/[controller]"); the nearest type declaring one wins
        for (var current = type; current is not null && mvc.Count == 0; current = current.BaseType)
        {
            foreach (var attribute in current.GetAttributes())
            {
                var name = attribute.AttributeClass?.ToDisplayString();
                var first = attribute.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
                if (first is null) continue;

                if (name == MvcRouteAttribute) mvc.Add(first);
                else if (name == WolverineRoutePrefixAttribute && ReferenceEquals(current, type)) wolverine = first.Trim('/');
            }
        }

        return (wolverine, mvc);
    }

    /// <summary>Wolverine's RoutePrefixPolicy: <c>/{prefix}/{route without its leading slash}</c>, no trailing slash.</summary>
    private static string wolverineTemplate(string? prefix, string template)
        => string.IsNullOrEmpty(prefix) ? template : $"/{prefix}/{template.TrimStart('/')}".TrimEnd('/');

    /// <summary>
    /// MVC attribute routing: a method template starting with <c>/</c> or <c>~/</c> stands alone, any other is
    /// appended to each controller <c>[Route]</c>. Always written with a leading slash. A template left with a
    /// token other than <c>[controller]</c> / <c>[action]</c> (an <c>[area]</c>) is skipped rather than guessed.
    /// </summary>
    private static IEnumerable<string> mvcTemplates(List<string> prefixes, string? template, INamedTypeSymbol type, IMethodSymbol method)
    {
        var candidates = new List<string>();
        if (template is not null && (template.StartsWith("/", StringComparison.Ordinal) || template.StartsWith("~/", StringComparison.Ordinal)))
        {
            candidates.Add(template.TrimStart('~'));
        }
        else if (prefixes.Count == 0)
        {
            if (template is not null) candidates.Add(template);
        }
        else
        {
            foreach (var prefix in prefixes)
            {
                candidates.Add(string.IsNullOrEmpty(template)
                    ? prefix
                    : prefix.TrimEnd('/') + "/" + template);
            }
        }

        var controller = type.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? type.Name.Substring(0, type.Name.Length - "Controller".Length)
            : type.Name;
        var action = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name.Substring(0, method.Name.Length - "Async".Length)
            : method.Name;

        foreach (var candidate in candidates)
        {
            var replaced = replaceToken(replaceToken(candidate, "[controller]", controller), "[action]", action);
            if (replaced.IndexOf('[') >= 0) continue;
            yield return "/" + replaced.TrimStart('/');
        }
    }

    private static string replaceToken(string text, string token, string value)
    {
        var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            text = text.Substring(0, at) + value + text.Substring(at + token.Length);
            at = text.IndexOf(token, at + value.Length, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static bool derivesFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.ToDisplayString() == metadataName)
                return true;

        return false;
    }

    /// <summary><c>WolverinePostAttribute</c> → POST, <c>HttpGetAttribute</c> → GET; anything else has no known verb.</summary>
    private static string? verbOf(string attributeName, string prefix)
    {
        if (!attributeName.StartsWith(prefix, StringComparison.Ordinal) || !attributeName.EndsWith("Attribute", StringComparison.Ordinal))
            return null;

        var verb = attributeName.Substring(prefix.Length, attributeName.Length - prefix.Length - "Attribute".Length).ToUpperInvariant();
        return verb is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS" or "QUERY" ? verb : null;
    }

    private static string? namedString(AttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(x => x.Key == name).Value.Value as string;
}
