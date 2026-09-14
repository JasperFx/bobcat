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
    internal const string StepAttribute = "BobcatStepAttribute";

    internal sealed class InterceptedCall
    {
        public string InterceptsLocation = "";
        public string DeclaringType = "";
        public string MethodName = "";
        public string ReturnType = "";
        public string Keyword = "";
        public string StepText = "";
        public bool ReturnsTask;
        public bool ReturnsVoid;
        public List<string> ParameterTypes = new();
        public List<string> ParameterNames = new();

        /// <summary>
        /// 0-based index of the marker comment this call sits under, within its own test method,
        /// or -1 when it sits under none (issue #304).
        /// </summary>
        public int DeclaredIndex = -1;
    }

    public static InterceptedCall? Extract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not InvocationExpressionSyntax invocation) return null;
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return null;

        var attribute = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == StepAttribute);
        if (attribute is null) return null;

        // RSEXPERIMENTAL002: GetInterceptableLocation and GetInterceptsLocationAttributeSyntax are
        // marked experimental by Roslyn, and there is no supported alternative — hand-writing the
        // encoded location is exactly what the API exists to stop people doing. Accepted knowingly
        // and confined to these two lines: if the shape changes, it changes here.
#pragma warning disable RSEXPERIMENTAL002
        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
#pragma warning restore RSEXPERIMENTAL002
        if (location is null) return null;

        var template = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? method.Name;
        var keyword = attribute.NamedArguments
            .FirstOrDefault(a => a.Key == "Keyword").Value.Value as string ?? "";

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
            Keyword = keyword,
            StepText = Render(template, method, invocation),
            DeclaredIndex = DeclaredIndexOf(invocation)
        };

        foreach (var parameter in method.Parameters)
        {
            call.ParameterTypes.Add(parameter.Type.ToDisplayString());
            call.ParameterNames.Add(parameter.Name);
        }

        return call;
    }

    /// <summary>
    /// <c>"the events are published on {threads} threads"</c> with <c>PublishMultiThreaded(3)</c>
    /// becomes <c>"the events are published on 3 threads"</c>.
    /// </summary>
    /// <remarks>
    /// Only a literal argument is substituted. An expression could be rendered as its source text,
    /// but a step reading "published on threadCount threads" is worse than one that still shows the
    /// placeholder — the reader can see that something was not resolved, rather than being told a
    /// variable name and believing it.
    /// </remarks>
    internal static string Render(string template, IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;

        for (var i = 0; i < method.Parameters.Length && i < arguments.Count; i++)
        {
            var placeholder = "{" + method.Parameters[i].Name + "}";
            if (!template.Contains(placeholder)) continue;

            var expression = arguments[i].Expression;
            if (expression is LiteralExpressionSyntax literal)
            {
                template = template.Replace(placeholder, literal.Token.ValueText);
            }
        }

        return template;
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
            var parameters = string.Join("", call.ParameterTypes
                .Select((t, i) => $", {t} {call.ParameterNames[i]}"));
            var arguments = string.Join(", ", call.ParameterNames);

            sb.AppendLine($"        {call.InterceptsLocation}");
            sb.AppendLine($"        internal static {call.ReturnType} __BobcatStep{index}(");
            sb.AppendLine($"            this global::{call.DeclaringType} receiver{parameters})");
            sb.AppendLine("        {");
            sb.AppendLine($"            var step = global::Bobcat.ScenarioRecorder.Step({Quote(call.Keyword)}, {Quote(call.StepText)}, {call.DeclaredIndex});");

            if (call.ReturnsTask)
            {
                // End the step when the helper's work ends, not when it hands back a Task.
                sb.AppendLine($"            return global::Bobcat.MarkerStepRuntime.Track(receiver.{call.MethodName}({arguments}), step);");
            }
            else if (call.ReturnsVoid)
            {
                // `return receiver.M();` is CS0127 on a void helper, in the CONSUMER's build and in
                // a file they cannot edit. It went unnoticed until #304's end-to-end test compiled
                // the first interceptor inside this repository: every earlier check read the
                // generated text, and Marten's helpers all return Task.
                sb.AppendLine("            using (step)");
                sb.AppendLine($"                receiver.{call.MethodName}({arguments});");
            }
            else
            {
                sb.AppendLine("            using (step)");
                sb.AppendLine($"                return receiver.{call.MethodName}({arguments});");
            }

            sb.AppendLine("        }");
            sb.AppendLine();
            index++;
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
