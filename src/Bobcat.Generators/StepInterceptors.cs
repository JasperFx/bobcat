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
        public List<string> ParameterTypes = new();
        public List<string> ParameterNames = new();
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
            Keyword = keyword,
            StepText = Render(template, method, invocation)
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
            sb.AppendLine($"            var step = global::Bobcat.ScenarioRecorder.Step({Quote(call.Keyword)}, {Quote(call.StepText)});");

            if (call.ReturnsTask)
            {
                // End the step when the helper's work ends, not when it hands back a Task.
                sb.AppendLine($"            return global::Bobcat.MarkerStepRuntime.Track(receiver.{call.MethodName}({arguments}), step);");
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
