using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bobcat.Generators;

/// <summary>
/// Issue #110: a call to a <c>[BobcatStep]</c> helper reports itself as a step, live, without the
/// test or the helper being edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>Interceptors, and three constraints the feature imposes rather than the design choosing.</b>
/// The interceptor's receiver parameter must be the method's DECLARING type — naming the calling
/// class is a signature mismatch. It must be an extension method, so it lives in a top-level
/// static class and cannot be tucked inside a partial of the test class. And therefore the helper
/// must be internal or public: an extension method cannot reach a protected member. That last one
/// is the whole adoption cost on an existing suite, and it is one word per helper.
/// </para>
/// <para>
/// <b>Why the generated wrapper awaits nothing.</b> It hands back whatever the helper returned and
/// ends the step when that completes, so the step's duration is the helper's real duration rather
/// than the time taken to hand back a Task.
/// </para>
/// </remarks>
internal static class StepInterceptors
{
    internal sealed class InterceptedCall
    {
        public string InterceptsLocation = "";
        public string DeclaringType = "";
        public string MethodName = "";
        public string ReturnType = "";
        public string Keyword = "";
        public string StepText = "";

        /// <summary>
        /// The step text as the attribute declares it, placeholders unresolved. What a PREVIEW shows
        /// — at preview time no argument has been evaluated, and a preview that filled the
        /// placeholders in would be describing a run that never happened.
        /// </summary>
        public string Template = "";
        public bool ReturnsTask;
        public bool ReturnsVoid;

        /// <summary>
        /// The helper answers <c>bool</c> (or <c>Task&lt;bool&gt;</c>) — a Storyteller Fact, whose
        /// answer IS the step's verdict.
        /// </summary>
        /// <remarks>
        /// Unconditional here, unlike the Gherkin lane, and the asymmetry is the point: a feature file
        /// can put an expected value in a cell, so there a bool return might be something to compare
        /// against. A C# call site cannot supply one implicitly — the caller wrote the arguments and
        /// read the answer — so a bool-returning step called from a test is always a fact.
        /// </remarks>
        public bool ReturnsBool;

        /// <summary>
        /// A projected ASSERTION rather than a declared step — <c>x.ShouldBe(7)</c>. Emitted only when
        /// the project opted in, and gathered rather than thrown so a run of them all get evaluated.
        /// </summary>
        public bool IsProjectedAssertion;

        /// <summary>The last assertion of its run: the point the run's failures are thrown at.</summary>
        public bool FlushesRun;

        /// <summary>The method's ORIGINAL definition, whose signature an interceptor must match.</summary>
        public IMethodSymbol? Definition;

        /// <summary>The receiver expression as written — the sentence's subject and the cell's name.</summary>
        public string Subject = "";

        /// <summary>
        /// The Bobcat comparison this assertion makes, or null when it makes none Bobcat has a
        /// member for — in which case NO cell is emitted at all (issue #384).
        /// </summary>
        public string? Comparison;
        public List<string> ParameterTypes = new();

        /// <summary>The parameters' own names — what a <c>{placeholder}</c> matches.</summary>
        public List<string> ParameterNames = new();

        /// <summary>
        /// The same names as C# IDENTIFIERS, keyword-escaped. A helper taking a parameter named
        /// <c>event</c> — which the Event Modeling vocabulary reaches for immediately — emitted
        /// <c>Type event</c> into the consumer's build and would not compile. The distinction is
        /// load-bearing rather than cosmetic: the placeholder is <c>{event}</c> and the expression
        /// is <c>@event</c>.
        /// </summary>
        public List<string> ParameterIdentifiers = new();

        /// <summary>
        /// Parameters whose <c>{placeholder}</c> the call site could not resolve at compile time,
        /// in template order — handed to the recorder so the RUNTIME value renders them
        /// (issue #339).
        /// </summary>
        public List<string> RuntimeArguments = new();

        /// <summary>
        /// Placeholders in the template that name no parameter at all. Nothing can ever fill one,
        /// so each is a BOBCAT027 warning rather than a silent <c>{typo}</c> on the canvas.
        /// </summary>
        public List<string> UnknownPlaceholders = new();

        /// <summary>The call site, for the diagnostic above.</summary>
        public Location? Location;

        /// <summary>
        /// 0-based index of the marker comment this call sits under, within its own test method,
        /// or -1 when it sits under none (issue #304).
        /// </summary>
        public int DeclaredIndex = -1;

        /// <summary>
        /// The <c>{Feature}/{Scenario}</c> identity of the test this call sits in, or null when it
        /// sits somewhere that is not a projected test — a helper calling another helper, a
        /// constructor, a class no <c>[BobcatFeature]</c> marks.
        /// </summary>
        public string? Uid;

        /// <summary>1-based line of the call site, so a preview can point at the source.</summary>
        public int Line;

        /// <summary>
        /// 0-based position of this call among the <c>[BobcatStep]</c> calls in its test method,
        /// filled once every call in the assembly is known — which is why it is not decided in
        /// <see cref="Extract"/>: one call site cannot see its siblings.
        /// </summary>
        public int PlannedIndex = -1;
    }

    public static InterceptedCall? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not InvocationExpressionSyntax invocation) return null;
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;

        // Any step attribute, not just the legacy [BobcatStep]: [Given], [When], [Then], [Check] and
        // the keywordless [Step] all reach a C# call site the same way (the merge).
        var recognized = StepAttributes.On(method);

        // ...or an ordinary assertion the project asked to have projected. Extracted unconditionally
        // and gated at emit time, because the transform cannot see MSBuild properties.
        if (recognized is null) return ExtractAssertion(ctx, invocation, method, ct);

        // RSEXPERIMENTAL002: GetInterceptableLocation and GetInterceptsLocationAttributeSyntax are
        // marked experimental by Roslyn, and there is no supported alternative — hand-writing the
        // encoded location is exactly what the API exists to stop people doing. Accepted knowingly
        // and confined to these two lines: if the shape changes, it changes here.
#pragma warning disable RSEXPERIMENTAL002
        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
#pragma warning restore RSEXPERIMENTAL002
        if (location is null) return null;

        var template = recognized.Expression.Length > 0 ? recognized.Expression : method.Name;

        // `Check` is not a word anyone writes in a sentence — it is Bobcat's name for a Then that
        // asserts on a bool. The step reads as Then.
        var keyword = recognized.Keyword == "Check" ? "Then" : recognized.Keyword;

        var call = new InterceptedCall
        {
#pragma warning disable RSEXPERIMENTAL002
            InterceptsLocation = location.GetInterceptsLocationAttributeSyntax(),
#pragma warning restore RSEXPERIMENTAL002
            DeclaringType = method.ContainingType.ToDisplayString(),
            MethodName = method.Name,
            ReturnType = method.ReturnType.ToDisplayString(),
            ReturnsTask = method.ReturnType.Name is "Task" or "ValueTask",
            ReturnsVoid = method.ReturnsVoid,
            ReturnsBool = returnsBool(method.ReturnType),
            Keyword = keyword,
            DeclaredIndex = DeclaredIndexOf(invocation),
            Uid = UidOf(invocation, ctx.SemanticModel, ct),
            Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Location = invocation.GetLocation()
        };

        foreach (var parameter in method.Parameters)
        {
            call.ParameterTypes.Add(parameter.Type.ToDisplayString());
            call.ParameterNames.Add(parameter.Name);
            call.ParameterIdentifiers.Add(Identifier(parameter.Name));
        }

        call.Template = template;
        call.StepText = Render(template, method, invocation, call.RuntimeArguments, call.UnknownPlaceholders);

        // A table is always handed over, whether or not the template names it. Every other argument
        // reaches the recorder because it is a word in the sentence; a StepTable is the step's DATA,
        // which renders as a grid under the sentence and never inside it — so a template that
        // mentions it is not the trigger, having one is.
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.ToDisplayString().TrimEnd('?') != "Bobcat.StepTable") continue;
            if (call.RuntimeArguments.Contains(parameter.Name)) continue;

            call.RuntimeArguments.Add(parameter.Name);
        }

        return call;
    }

    /// <summary>
    /// <c>"the events are published on {threads} threads"</c> with <c>PublishMultiThreaded(3)</c>
    /// becomes <c>"the events are published on 3 threads"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A literal argument is substituted here, at compile time, because it is the same string on
    /// every run. Everything else is deferred: the parameter's name goes into
    /// <paramref name="runtimeArguments"/> and the generated interceptor hands the VALUE to
    /// <c>ScenarioRecorder.Step</c>, which renders it (issue #339).
    /// </para>
    /// <para>
    /// That is the whole of a typed store vocabulary — types, minted ids, constructed command
    /// objects — and until #339 none of it could bind: only the syntax was available here, and a
    /// step reading "published on threadCount threads" is worse than one showing the placeholder.
    /// The value is available at execution time, which is also when a step's real data is worth
    /// showing, so the choice was never between a variable NAME and a placeholder.
    /// </para>
    /// <para>
    /// <b>An <c>out</c> or <c>ref</c> parameter is not deferred.</b> Its value does not exist
    /// until the helper has run, and the step text is reported before the call.
    /// </para>
    /// </remarks>
    internal static string Render(
        string template,
        IMethodSymbol method,
        InvocationExpressionSyntax invocation,
        List<string>? runtimeArguments = null,
        List<string>? unknownPlaceholders = null)
    {
        var arguments = invocation.ArgumentList.Arguments;

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            var placeholder = "{" + parameter.Name + "}";
            if (!template.Contains(placeholder)) continue;

            // A literal used to be substituted right here, at compile time, because it is the same
            // string on every run. It no longer is, and the reason is rendering: a value substituted
            // here leaves no SPAN behind, and a span is how a renderer knows where in the sentence a
            // value sits — which is where Storyteller put a comparison's verdict and where Bobcat now
            // puts it too. With the shortcut in place, whether a cell rendered inside its sentence or
            // on a line underneath came down to whether the CALLER happened to write a positional
            // literal or a named argument, which is not a distinction any reader could be expected to
            // see. Deferring every value also follows #339's own argument: the value at execution time
            // is the thing worth showing, not the spelling at the call site.
            //
            // The cost is one StepArgument[] per step that used to allocate nothing. That is the
            // price of a sentence that can carry its own verdicts.
            if (parameter.RefKind is RefKind.Out or RefKind.Ref) continue;

            runtimeArguments?.Add(parameter.Name);
        }

        if (unknownPlaceholders != null)
        {
            var names = new HashSet<string>(method.Parameters.Select(x => x.Name));
            foreach (var placeholder in PlaceholdersIn(template))
            {
                if (!names.Contains(placeholder)) unknownPlaceholders.Add(placeholder);
            }
        }

        return template;
    }

    /// <summary>
    /// The <c>{name}</c> placeholders left in a template, in order. Deliberately narrow — letters,
    /// digits and underscore — so prose in a step's text cannot be mistaken for one.
    /// </summary>
    internal static IEnumerable<string> PlaceholdersIn(string template)
    {
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;

            var close = template.IndexOf('}', i + 1);
            if (close < 0) break;

            var name = template.Substring(i + 1, close - i - 1);
            i = close;

            if (name.Length == 0) continue;
            if (!IsIdentifier(name)) continue;

            yield return name;
        }
    }

    private static bool IsIdentifier(string name)
    {
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }

        return !char.IsDigit(name[0]);
    }

    /// <summary>
    /// Which marker comment of the enclosing test this call runs under (issue #304) — the last
    /// one declared at or above the call's own line, 0-based, or -1 when the call is under none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decided here because here is the only place both facts are exact.</b> A comment is
    /// erased by the compiler and an interceptor is generated per call site, so at build time the
    /// generator knows precisely which sentence a call falls under; at runtime it would have to
    /// read a stack trace and hope. That is the difference between attribution and inference, and
    /// <c>docs/marker-steps.md</c>'s "declared is not executed" rests on it.
    /// </para>
    /// <para>
    /// <b>Scoped to the enclosing METHOD, and only if that method is a test.</b> A decorated helper
    /// called from another helper is under no narrative of its own — the comments that would be in
    /// scope belong to a different method — so it reports -1 and attaches to nothing. The same
    /// answer covers a call from a fixture, a constructor, or a class the feature attribute never
    /// marked, and the runtime bounds-checks the index against what was actually registered.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The <c>{Feature}/{Scenario}</c> identity of the projected test a call sits in, or null.
    /// </summary>
    /// <remarks>
    /// The same derivation <c>MarkerSpecNaming</c> performs at runtime, applied to the syntax rather
    /// than to reflection — so a planned step and the recorded step it turns into key on the same
    /// string with no mapping table, the rule the whole projected lane rests on.
    /// </remarks>
    internal static string? UidOf(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct)
    {
        var method = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null || !MarkerCommentSpecs.IsTestMethod(method)) return null;

        var declaration = method.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (declaration is null) return null;

        if (!MarkerCommentSpecs.TryFeatureTitle(declaration, model, ct, out var feature)) return null;

        return feature + "/" + MarkerSpecNaming.ScenarioTitle(method.Identifier.ValueText);
    }

    /// <summary>
    /// A statement-level Shouldly call, as a step that gathers rather than throws.
    /// </summary>
    private static InterceptedCall? ExtractAssertion(
        GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation, IMethodSymbol method,
        CancellationToken ct)
    {
        if (ProjectedAssertions.DialectFor(method) is not { } dialect) return null;

        // Never a call whose value is consumed: see ProjectedAssertions for why chaining must be left
        // alone.
        if (ProjectedAssertions.StatementOf(invocation) is not { } statement) return null;

        var uid = UidOf(invocation, ctx.SemanticModel, ct);
        if (uid is null) return null;

#pragma warning disable RSEXPERIMENTAL002
        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
#pragma warning restore RSEXPERIMENTAL002
        if (location is null) return null;

        // `x.ShouldBe(7)` resolves to the REDUCED extension method, whose Parameters omit the receiver
        // — so an interceptor built from it is one parameter short and CS9144's. ReducedFrom is the
        // declared method, receiver included, which is the signature an interceptor has to match.
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;

        var call = new InterceptedCall
        {
#pragma warning disable RSEXPERIMENTAL002
            InterceptsLocation = location.GetInterceptsLocationAttributeSyntax(),
#pragma warning restore RSEXPERIMENTAL002
            DeclaringType = definition.ContainingType.ToDisplayString(),
            MethodName = definition.Name,
            ReturnType = definition.ReturnType.ToDisplayString(),
            ReturnsVoid = definition.ReturnsVoid,
            Keyword = "Then",
            IsProjectedAssertion = true,
            FlushesRun = ProjectedAssertions.IsLastOfRun(statement, ctx.SemanticModel, ct),
            Definition = definition,
            Uid = uid,
            DeclaredIndex = DeclaredIndexOf(invocation),
            Line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Location = invocation.GetLocation()
        };

        call.Subject = dialect.Subject(method, invocation);
        call.Comparison = dialect.ComparisonOf(method);
        call.Template = dialect.Sentence(method, invocation);
        call.StepText = call.Template;

        return call;
    }

    /// <summary>Whether the method answers <c>bool</c>, through a <c>Task</c>/<c>ValueTask</c> or not.</summary>
    private static bool returnsBool(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol { Name: "Task" or "ValueTask" } awaitable
            && awaitable.TypeArguments.Length == 1)
        {
            returnType = awaitable.TypeArguments[0];
        }

        return returnType.SpecialType == SpecialType.System_Boolean;
    }

    internal static int DeclaredIndexOf(InvocationExpressionSyntax invocation)
    {
        var method = invocation.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null || !MarkerCommentSpecs.IsTestMethod(method)) return -1;

        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        var index = -1;
        var found = 0;
        foreach (var step in MarkerCommentSpecs.StepsIn(method))
        {
            if (step.Line <= line) index = found;
            found++;
        }

        return index;
    }

    /// <summary>
    /// Interceptors are opted into per namespace, so the emitted namespace is a CONSTANT rather
    /// than derived from the assembly: a project enables the feature with one predictable line,
    /// identical everywhere, instead of a name that changes per project (and that an assembly name
    /// like "marker-sample" cannot even spell as an identifier).
    /// </summary>
    internal const string Namespace = "Bobcat.Generated";

    /// <summary>
    /// Number every call by its position among the <c>[BobcatStep]</c> calls in its own test method,
    /// in source order. Must run before <see cref="Emit"/>, which writes the ordinal into the
    /// interceptor, and before <see cref="EmitPlan"/>, which writes the plan it indexes into.
    /// </summary>
    /// <remarks>
    /// Source order is by LINE, not by the order the incremental generator happened to hand the
    /// call sites over — that order is an implementation detail of Roslyn's caching and would make a
    /// scenario's plan reshuffle between builds of unchanged source.
    /// </remarks>
    public static void Number(IReadOnlyList<InterceptedCall> calls)
    {
        foreach (var scenario in calls.Where(c => c.Uid != null).GroupBy(c => c.Uid))
        {
            var index = 0;
            foreach (var call in scenario.OrderBy(c => c.Line))
            {
                call.PlannedIndex = index++;
            }
        }
    }

    /// <summary>
    /// The plan a projected test's grammar calls make up, registered for
    /// <see cref="Bobcat.PlannedSteps"/> to serve — what a preview shows, and what tells a stopped
    /// scenario which steps it never reached.
    /// </summary>
    public static string EmitPlan(IReadOnlyList<InterceptedCall> calls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {Namespace}");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class BobcatPlannedStepRegistration");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");

        // Armed here rather than when the first scenario starts, because a PREVIEW runs no
        // scenarios: `--list-tests` discovers and exits, and module initializers are the only thing
        // that has run by then. Idempotent, and a no-op unless the environment asks for it.
        sb.AppendLine("            global::Bobcat.ProjectedSpecConsole.EnableIfRequested();");
        sb.AppendLine();

        foreach (var scenario in calls.Where(c => c.Uid != null)
                     .GroupBy(c => c.Uid!)
                     .OrderBy(g => g.Key, System.StringComparer.Ordinal))
        {
            var steps = string.Join(", ", scenario.OrderBy(c => c.PlannedIndex).Select(call =>
                "new global::Bobcat.PlannedStep("
                + $"{Quote(call.Keyword)}, {Quote(call.Template)}, "
                + $"{Quote(ShortTypeName(call.DeclaringType) + "." + call.MethodName)}, "
                + (call.DeclaredIndex >= 0 ? (call.DeclaredIndex + 1).ToString() : "null")
                + $", {call.Line})"));

            sb.AppendLine($"            global::Bobcat.PlannedSteps.Register({Quote(scenario.Key)}, {steps});");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>True when any call site sits in a projected test — the gate on emitting the plan.</summary>
    public static bool HasPlan(IEnumerable<InterceptedCall> calls) => calls.Any(c => c.Uid != null);

    /// <summary>The declaring type without its namespace, which is how a binding reads best.</summary>
    internal static string ShortTypeName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
    }

    /// <summary>
    /// An interceptor for a projected assertion. Its signature has to match the ASSERTION's, not
    /// Bobcat's — type parameters, constraints, optional arguments and all — because that is what the
    /// interceptor feature requires.
    /// </summary>
    /// <remarks>
    /// The original is called STATICALLY through its declaring type rather than as an extension method.
    /// Generated code is not itself intercepted, so recursion was never possible, but the static call
    /// says so at a glance and cannot be broken by a later change to what gets intercepted.
    /// </remarks>
    private static void emitAssertion(StringBuilder sb, InterceptedCall call, int index)
    {
        var definition = call.Definition!;

        var typeParameters = definition.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", definition.TypeParameters.Select(t => t.Name)) + ">";

        var parameters = definition.Parameters.Select((p, i) => parameterOf(p, i == 0)).ToList();
        var arguments = definition.Parameters.Select(p => Identifier(p.Name)).ToList();

        sb.AppendLine($"        {call.InterceptsLocation}");
        sb.AppendLine($"        internal static void __BobcatAssert{index}{typeParameters}(");
        sb.AppendLine($"            {string.Join(", ", parameters)})");

        foreach (var clause in constraintsOf(definition))
        {
            sb.AppendLine($"            {clause}");
        }

        sb.AppendLine("        {");
        sb.AppendLine(
            $"            var step = global::Bobcat.ScenarioRecorder.Step({Quote(call.Keyword)}, "
            + $"{Quote(call.StepText)}, {call.DeclaredIndex}, {call.PlannedIndex});");
        // The receiver is the subject; the first parameter after it that is not a custom message is the
        // expectation, when there is one. Both as VALUES, so the cell is data rather than parsed prose.
        var receiver = arguments.Count > 0 ? arguments[0] : "null";
        var expectation = definition.Parameters
            .Select((p, i) => (p, i))
            .Where(x => x.i > 0 && x.p.Name is not ("customMessage" or "customMessageFunc"))
            .Select(x => Identifier(x.p.Name))
            .FirstOrDefault() ?? "null";

        var invoke = $"() => global::{call.DeclaringType}.{call.MethodName}{typeParameters}"
                     + $"({string.Join(", ", arguments)})";
        var flush = call.FlushesRun ? "true" : "false";

        // No comparison means NO CELL, and that is the closed enum doing its work rather than a
        // shortfall (issue #384). ShouldBeTrue, ShouldBeEquivalentTo and ShouldBeOfType have no
        // expected/actual pair that any row shape could state truthfully, so the step renders as a
        // plain line with its verdict and duration and says nothing it cannot support. The choice
        // is made HERE, by picking the overload, so the runtime never has to decide whether a cell
        // it was handed is describable.
        sb.AppendLine(call.Comparison is null
            ? $"            global::Bobcat.AssertionRun.Gather({invoke}, step, {flush});"
            : $"            global::Bobcat.AssertionRun.Gather({invoke}, step, {flush}, "
              + $"{Quote(call.Subject)}, {receiver}, {expectation}, "
              + $"global::Bobcat.Engine.Comparison.{call.Comparison});");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    /// <summary>
    /// Fully qualified AND nullability-annotated. The plain fully-qualified form drops <c>?</c>, and an
    /// interceptor whose <c>string</c> should have been <c>string?</c> is CS9159 in the consumer's build
    /// — a warning in a file they cannot edit.
    /// </summary>
    private static readonly SymbolDisplayFormat interceptorFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>One parameter of the intercepted signature, reproduced exactly.</summary>
    private static string parameterOf(IParameterSymbol parameter, bool isReceiver)
    {
        var modifiers = isReceiver ? "this " : parameter.IsParams ? "params " : "";
        var text = $"{modifiers}{parameter.Type.ToDisplayString(interceptorFormat)} " + Identifier(parameter.Name);

        // `this T actual = default` is CS1743 — a receiver cannot carry one even when the original
        // declares it, and the call site always supplies it anyway.
        if (isReceiver || !parameter.HasExplicitDefaultValue) return text;

        return text + " = " + defaultOf(parameter);
    }

    /// <summary>
    /// An optional parameter's default, which the interceptor must repeat or the call site no longer
    /// matches.
    /// </summary>
    private static string defaultOf(IParameterSymbol parameter)
        => parameter.ExplicitDefaultValue switch
        {
            null => "default",
            bool flag => flag ? "true" : "false",
            string text => Quote(text),
            char character => "'" + character + "'",

            // Anything else — an enum, a decimal, a number whose literal form differs by locale — as
            // `default`, which is legal for every optional parameter and is what the value is in every
            // case that matters here.
            var value when value.GetType().IsPrimitive => value.ToString()!.ToLowerInvariant(),
            _ => "default"
        };

    /// <summary>
    /// The <c>where</c> clauses of the intercepted signature. Omitting a constraint the original
    /// declares is a build error in the consumer's own compilation, so they are reproduced rather than
    /// hoped about.
    /// </summary>
    private static IEnumerable<string> constraintsOf(IMethodSymbol definition)
    {
        foreach (var parameter in definition.TypeParameters)
        {
            var constraints = new List<string>();

            if (parameter.HasReferenceTypeConstraint) constraints.Add("class");
            if (parameter.HasValueTypeConstraint) constraints.Add("struct");
            if (parameter.HasUnmanagedTypeConstraint) constraints.Add("unmanaged");
            if (parameter.HasNotNullConstraint) constraints.Add("notnull");

            constraints.AddRange(parameter.ConstraintTypes.Select(t => t.ToDisplayString(interceptorFormat)));

            if (parameter.HasConstructorConstraint) constraints.Add("new()");

            if (constraints.Count > 0) yield return $"where {parameter.Name} : {string.Join(", ", constraints)}";
        }
    }

    public static string Emit(IEnumerable<InterceptedCall> calls)
    {
        const string ns = Namespace;
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    file static class BobcatStepInterceptors");
        sb.AppendLine("    {");

        var index = 0;
        foreach (var call in calls)
        {
            if (call.IsProjectedAssertion)
            {
                emitAssertion(sb, call, index++);
                continue;
            }

            var parameters = string.Join("", call.ParameterTypes
                .Select((t, i) => $", {t} {call.ParameterIdentifiers[i]}"));
            var arguments = string.Join(", ", call.ParameterIdentifiers);

            sb.AppendLine($"        {call.InterceptsLocation}");
            sb.AppendLine($"        internal static {call.ReturnType} __BobcatStep{index}(");
            sb.AppendLine($"            this global::{call.DeclaringType} receiver{parameters})");
            sb.AppendLine("        {");
            // The values, not the syntax (issue #339). Only the placeholders that survived
            // compile-time substitution are passed — a step with none allocates nothing.
            var values = call.RuntimeArguments.Count == 0
                ? ""
                : ", new global::Bobcat.StepArgument[] { "
                  + string.Join(", ", call.RuntimeArguments.Select(name => $"new({Quote(name)}, {Identifier(name)})"))
                  + " }";

            sb.AppendLine($"            var step = global::Bobcat.ScenarioRecorder.Step({Quote(call.Keyword)}, {Quote(call.StepText)}, {call.DeclaredIndex}, {call.PlannedIndex}{values});");

            if (call.ReturnsTask && call.ReturnsBool)
            {
                // An asynchronous Fact: the answer decides the verdict, and the step ends when the
                // work ends so it still carries a real duration.
                sb.AppendLine($"            return global::Bobcat.MarkerStepRuntime.TrackFact(receiver.{call.MethodName}({arguments}), step);");
            }
            else if (call.ReturnsTask)
            {
                // End the step when the helper's work ends, not when it hands back a Task.
                sb.AppendLine($"            return global::Bobcat.MarkerStepRuntime.Track(receiver.{call.MethodName}({arguments}), step);");
            }
            else if (call.ReturnsBool)
            {
                // A synchronous Fact. The answer flows through to the caller untouched.
                emitSynchronousCall(sb,
                    $"return global::Bobcat.MarkerStepRuntime.Fact(receiver.{call.MethodName}({arguments}), step);");
            }
            else if (call.ReturnsVoid)
            {
                // `return receiver.M();` is CS0127 on a void helper, in the CONSUMER's build and in
                // a file they cannot edit. It went unnoticed until #304's end-to-end test compiled
                // the first interceptor inside this repository: every earlier check read the
                // generated text, and Marten's helpers all return Task.
                emitSynchronousCall(sb, $"receiver.{call.MethodName}({arguments});");
            }
            else
            {
                emitSynchronousCall(sb, $"return receiver.{call.MethodName}({arguments});");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            index++;
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// A synchronous helper call, bracketed so that an exception becomes the STEP's verdict and not
    /// only the test's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <c>using (step)</c>.</b> It was, and a step that threw rendered green. Disposal
    /// runs on the way out of a <c>using</c> either way, and disposal alone means "the step ended" —
    /// nothing in it says the step ended badly. The scenario was still reported red by the runner, so
    /// the only visible symptom was a specification whose failing line was the one line marked
    /// <c>✓</c>; it surfaced the first time a projected spec was rendered to a console rather than
    /// only published. The asynchronous path never had the bug because
    /// <c>MarkerStepRuntime.Track</c> has always caught and reported — this is the same two lines,
    /// inline, for the path that cannot await.
    /// </para>
    /// <para>
    /// Rethrown unchanged: whether a failed step ends the test is the test framework's decision, and
    /// swallowing here would quietly turn every projected step into a continue-on-error one.
    /// </para>
    /// </remarks>
    private static void emitSynchronousCall(StringBuilder sb, string invocation)
    {
        sb.AppendLine("            try");
        sb.AppendLine("            {");
        sb.AppendLine($"                {invocation}");
        sb.AppendLine("            }");
        sb.AppendLine("            catch (global::System.Exception e)");
        sb.AppendLine("            {");
        sb.AppendLine("                (step as global::Bobcat.IStepHandle)?.Fail(e);");
        sb.AppendLine("                throw;");
        sb.AppendLine("            }");
        sb.AppendLine("            finally");
        sb.AppendLine("            {");
        sb.AppendLine("                step.Dispose();");
        sb.AppendLine("            }");
    }

    /// <summary>The name as it can be written in generated code.</summary>
    internal static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
