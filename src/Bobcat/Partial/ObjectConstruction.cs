using System.Reflection;
using Bobcat.Engine;
using Bobcat.Runtime;

namespace Bobcat.Partial;

/// <summary>
/// Builds an object from the members a partial object specifies (bobcat#417): records, classes with
/// setters or <c>init</c> accessors, <c>required</c> members, structs, and F# records, options, lists
/// and single-case unions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Constructor first, then members.</b> The public constructor binding the most specified members
/// wins; a specified member it does not take is assigned afterwards through its setter or
/// <c>init</c> accessor. A record with a primary constructor <em>and</em> <c>init</c> properties is
/// the case that needs both — <c>RecordBuilding</c> sets nothing once a constructor binds.
/// </para>
/// <para>
/// <b>What is a spec defect, by name.</b> A member that matches nothing on the type (a typo, or a
/// rename since the spec was written), and a specified member nothing can set (read-only and taken
/// by no constructor). Both throw <see cref="SpecCriticalException"/> naming the member, because the
/// specification is asking for something the type cannot be.
/// </para>
/// </remarks>
internal static class ObjectConstruction
{
    /// <summary>How deep a path, or an unspecified nested object, is followed.</summary>
    public const int MaxDepth = 8;

    public static object Build(Type type, IReadOnlyList<SpecifiedValue> values, IUnspecifiedValues unspecified,
        string? step)
        => new Builder(unspecified, step).Build(type, values, "", 0)!;

    private sealed class Builder(IUnspecifiedValues unspecified, string? step)
    {
        private readonly HashSet<Type> _underConstruction = new();

        public object? Build(Type type, IReadOnlyList<SpecifiedValue> values, string prefix, int depth)
        {
            if (depth > MaxDepth)
                throw defect($"'{prefix}' is more than {MaxDepth} levels deep");

            // Every type on the way down, so filling an unspecified member never builds a type
            // that is already part way through being built — a cycle ends in null, not a stack overflow.
            var added = _underConstruction.Add(type);
            try
            {
                return buildCore(type, values, prefix, depth);
            }
            finally
            {
                if (added) _underConstruction.Remove(type);
            }
        }

        private object buildCore(Type type, IReadOnlyList<SpecifiedValue> values, string prefix, int depth)
        {
            var wrapper = FSharpShapes.SingleCaseWrapper(type);
            if (wrapper is not null && values.Count > 0)
                throw defect($"{type.Name} is an F# single-case union, so specify its value directly rather than its members");

            var members = Members.Of(type);
            var segments = group(type, values, prefix, members);
            var constructor = choose(type, segments, members);

            var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object instance;

            if (constructor is null)
            {
                instance = Activator.CreateInstance(type)!;
            }
            else
            {
                var parameters = constructor.GetParameters();
                var arguments = new object?[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var segment = segments.FirstOrDefault(s => Members.Binds(parameter, s.Name, members));
                    var path = join(prefix, segment?.Name ?? propertyNameFor(parameter, members));

                    if (segment is not null)
                    {
                        bound.Add(segment.Name);
                        arguments[i] = valueOf(segment, parameter.ParameterType, path, depth);
                    }
                    else
                    {
                        arguments[i] = defaultFor(parameter, type, path, depth);
                    }
                }

                instance = invoke(constructor, arguments);
            }

            foreach (var segment in segments.Where(s => !bound.Contains(s.Name)))
            {
                var member = members.Find(segment.Name)!;
                member.Set(instance, valueOf(segment, member.Type, join(prefix, member.Name), depth));
                bound.Add(segment.Name);
            }

            fillUnsetMembers(type, instance, members, constructor, bound, prefix, depth);
            return instance;
        }

        /// <summary>
        /// A required member nothing set, or a non-nullable reference member left null by the type's
        /// own initializers, gets the policy's value. A member the type initialized itself is left
        /// alone: that default is the author's, and overwriting it would be the spec's invention.
        /// </summary>
        private void fillUnsetMembers(Type type, object instance, Members members, ConstructorInfo? constructor,
            HashSet<string> bound, string prefix, int depth)
        {
            var setsRequired = constructor?.CustomAttributes.Any(a =>
                a.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") ?? false;

            foreach (var member in members.Writable)
            {
                if (bound.Contains(member.Name)) continue;
                if (constructor is not null && constructor.GetParameters()
                        .Any(p => string.Equals(p.Name, member.Name, StringComparison.OrdinalIgnoreCase))) continue;

                var required = member.IsRequired && !setsRequired;
                var leftNull = !member.Type.IsValueType && !member.AllowsNull && member.Get(instance) is null;
                if (!required && !leftNull) continue;

                member.Set(instance, fill(new UnspecifiedMember(type, join(prefix, member.Name), member.Type, member.AllowsNull), depth));
            }
        }

        private object? defaultFor(ParameterInfo parameter, Type declaring, string path, int depth)
        {
            if (parameter.HasDefaultValue && parameter.DefaultValue is not DBNull)
            {
                // `= default` on a value type reports a null DefaultValue; materialize default(T).
                return parameter.DefaultValue is null && parameter.ParameterType.IsValueType
                       && Nullable.GetUnderlyingType(parameter.ParameterType) is null
                    ? Activator.CreateInstance(parameter.ParameterType)
                    : parameter.DefaultValue;
            }

            return fill(new UnspecifiedMember(declaring, path, parameter.ParameterType, Nullability.AllowsNull(parameter)), depth);
        }

        private object? fill(UnspecifiedMember member, int depth)
        {
            if (unspecified.TryValueFor(member, out var value)) return value;

            var type = member.Type;
            if (type.IsValueType) return Activator.CreateInstance(type);
            if (FSharpShapes.SingleCaseWrapper(type) is { } wrapper)
            {
                var field = wrapper.GetParameters()[0].ParameterType;
                return wrapper.Invoke(null, [fill(member with { Type = field, AllowsNull = false }, depth)]);
            }

            // A reference type with nothing specified: build it empty, unless that would recurse
            // into itself or it cannot be built — then null, which is all "unspecified" can mean.
            if (type.IsAbstract || type.IsInterface || depth >= MaxDepth || _underConstruction.Contains(type)) return null;
            try
            {
                return Build(type, [], member.Path, depth + 1);
            }
            catch (SpecCriticalException)
            {
                return null;
            }
        }

        private object? valueOf(Segment segment, Type target, string path, int depth)
        {
            if (segment.Nested.Count == 0) return SpecifiedValues.Convert(segment.Direct!, target, path);

            var nestedType = FSharpShapes.OptionValueType(target) ?? Nullable.GetUnderlyingType(target) ?? target;
            var nested = Build(nestedType, segment.Nested, path, depth + 1);
            return FSharpShapes.OptionValueType(target) is null ? nested : FSharpShapes.Some(target, nested);
        }

        private List<Segment> group(Type type, IReadOnlyList<SpecifiedValue> values, string prefix, Members members)
        {
            var segments = new List<Segment>();
            foreach (var value in values)
            {
                var path = value.Path.Trim();
                if (path.Contains('[') || path.Contains(']'))
                    throw defect($"'{join(prefix, path)}' indexes into a collection. Specify the collection as a whole instead");

                var dot = path.IndexOf('.');
                var head = dot < 0 ? path : path[..dot];
                var segment = segments.FirstOrDefault(s => string.Equals(s.Name, head, StringComparison.OrdinalIgnoreCase));
                if (segment is null)
                {
                    segment = new Segment(head);
                    segments.Add(segment);
                }

                if (dot < 0) segment.Direct = value;
                else segment.Nested.Add(value with { Path = path[(dot + 1)..] });

                if (segment.Direct is not null && segment.Nested.Count > 0)
                    throw defect($"'{join(prefix, head)}' is specified both as a whole and by its members. Specify one or the other");
            }

            var unknown = segments
                .Where(s => members.Find(s.Name) is null && !Members.AnyConstructorTakes(type, s.Name, members))
                .Select(s => join(prefix, s.Name))
                .ToList();

            if (unknown.Count > 0)
                throw defect(
                    $"the member{(unknown.Count == 1 ? "" : "s")} [{string.Join(", ", unknown)}] "
                    + $"{(unknown.Count == 1 ? "matches" : "match")} nothing on '{type.Name}', which has ({string.Join(", ", members.Names(type))}). "
                    + "Check the spelling, or the member may have been renamed since this spec was written");

            return segments;
        }

        /// <summary>
        /// The constructor to build with, or null for a struct's implicit one. Among constructors that
        /// can reach every specified member — by a parameter, or a settable member afterwards — prefer
        /// the one binding the most, then the one leaving fewest parameters to fill, then the shortest.
        /// </summary>
        private ConstructorInfo? choose(Type type, List<Segment> segments, Members members)
        {
            var candidates = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Select(c => (Constructor: (ConstructorInfo?)c, Parameters: c.GetParameters()))
                .ToList();

            if (type.IsValueType) candidates.Add((null, []));

            var ranked = candidates
                .Select(c =>
                {
                    var unreached = segments
                        .Where(s => !c.Parameters.Any(p => Members.Binds(p, s.Name, members))
                                    && members.Find(s.Name) is not { CanWrite: true })
                        .ToList();
                    var bindCount = segments.Count(s => c.Parameters.Any(p => Members.Binds(p, s.Name, members)));
                    var toFill = c.Parameters.Count(p => !p.HasDefaultValue
                                                         && !segments.Any(s => Members.Binds(p, s.Name, members)));
                    return (c.Constructor, Unreached: unreached, Bound: bindCount, ToFill: toFill, Length: c.Parameters.Length);
                })
                .ToList();

            var feasible = ranked.Where(r => r.Unreached.Count == 0)
                .OrderByDescending(r => r.Bound)
                .ThenBy(r => r.ToFill)
                .ThenBy(r => r.Length)
                .ToList();

            if (feasible.Count > 0) return feasible[0].Constructor;

            if (ranked.Count == 0)
                throw defect($"'{type.Name}' has no public constructor to build it with");

            var closest = ranked.OrderBy(r => r.Unreached.Count).ThenByDescending(r => r.Bound).First();
            var names = closest.Unreached.Select(s => members.Find(s.Name)?.Name ?? s.Name).ToList();
            throw defect(
                $"{string.Join(", ", names.Select(n => $"'{type.Name}.{n}'"))} "
                + $"{(names.Count == 1 ? "is" : "are")} read-only and no public constructor takes "
                + $"{(names.Count == 1 ? "it" : "them")} alongside the other specified members, so a specification cannot set "
                + $"{(names.Count == 1 ? "it" : "them")}");
        }

        private static string propertyNameFor(ParameterInfo parameter, Members members)
            => members.Find(parameter.Name!)?.Name ?? parameter.Name!;

        private static object invoke(ConstructorInfo constructor, object?[] arguments)
        {
            try
            {
                return constructor.Invoke(arguments);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }

        private SpecCriticalException defect(string problem)
            => new((step is null ? "" : $"'{step}': ") + problem + ".");

        private static string join(string prefix, string name) => prefix.Length == 0 ? name : $"{prefix}.{name}";
    }

    private sealed class Segment(string name)
    {
        public string Name { get; } = name;
        public SpecifiedValue? Direct { get; set; }
        public List<SpecifiedValue> Nested { get; } = new();
    }
}
