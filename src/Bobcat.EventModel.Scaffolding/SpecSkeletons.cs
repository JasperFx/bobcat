using System.Text;

namespace Bobcat.EventModel.Scaffolding;

/// <summary>
/// The specification skeletons a spec-ownership manifest asks for (issue #324 part 4) — the
/// code-first and projected counterparts of the <c>.feature</c> the Gherkin lane gets.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scenario bodies travel as comments and the method throws</b> — the same discipline
/// <see cref="ScaffoldFrame"/> applies to a slice's code (issue #226). A skeleton whose body was
/// left empty would be a GREEN spec for an unimplemented slice, which is worse than no spec at
/// all; one whose body was a syntax hole would fail the whole project. Throwing gives the correct
/// spec-first state: every scaffolded spec runs on day one, fails on its own assertion, and names
/// the slice that owns the failure.
/// </para>
/// <para>
/// <b>One file per <c>owner:</c>, not per slice.</b> Several slices may name one owner — the case
/// part 1's method-level <c>[BobcatSlice]</c> exists for — so the owner is the grouping key and a
/// shared owner gets class-level <c>[BobcatFeature]</c> with per-test bindings under it.
/// </para>
/// </remarks>
public static class SpecSkeletons
{
    public static IReadOnlyDictionary<string, string> Scaffold(CuratedModelFile model, SpecOwnershipPlan ownership)
    {
        var files = new Dictionary<string, string>();
        var bySlice = model.Slices.ToDictionary(x => x.Name, StringComparer.Ordinal);

        // Whether a slice is written at all is `scaffold:` — a declaration, resolved once in
        // SpecOwnershipPlan (issue #334). The filter here used to be "unit or code-first", which
        // is the Marten DaemonTests row read as a rule: a projected integration slice was never
        // written, because that row assumed an existing suite was adopting the slice. For a repo
        // being built it is the opposite, and the two intents are not derivable from anything the
        // scaffolder can see — so the manifest says which.
        foreach (var group in ownership.OwnersIn(model))
        {
            var entries = group.Where(x => bySlice.ContainsKey(x.Slice)).ToList();
            if (entries.Count == 0) continue;

            var slices = entries.Select(x => bySlice[x.Slice]).ToList();
            var authoring = entries[0].Authoring;

            var typeName = TypeNameOf(group.Key);
            var ns = NamespaceOf(group.Key) ?? $"{model.Namespace ?? model.Model}.Specs";

            var plans = slices.Select(x => SliceScaffolder.PlanFor(model, x)).ToList();

            files[$"Specs/{typeName}.cs"] = authoring == SpecAuthoring.CodeFirst
                ? codeFirst(model, ns, typeName, entries, slices, plans)
                : projected(model, ns, typeName, entries, slices, plans);
        }

        return files;
    }

    /// <inheritdoc cref="ProjectedSpecNaming.MethodNameFor"/>
    public static string MethodNameFor(string scenarioName) => ProjectedSpecNaming.MethodNameFor(scenarioName);

    /// <inheritdoc cref="ProjectedSpecNaming.RoundTrips"/>
    public static bool RoundTrips(string scenarioName) => ProjectedSpecNaming.RoundTrips(scenarioName);

    public static string TypeNameOf(string owner)
    {
        var dot = owner.LastIndexOf('.');
        return dot < 0 ? owner : owner.Substring(dot + 1);
    }

    public static string? NamespaceOf(string owner)
    {
        var dot = owner.LastIndexOf('.');
        return dot < 0 ? null : owner.Substring(0, dot);
    }

    /// <summary>
    /// <c>SliceType = typeof(X)</c> where a type bears the slice's name, <c>SliceName</c> where
    /// none does. The model is what knows: a Command slice's command record and a View slice's
    /// read model are named for the slice (16 of CritterCrush's 19), and an Automation's only type
    /// is <c>{Slice}Handler</c>, which is precisely the type <c>SliceType</c> must not be given.
    /// </summary>
    public static string BindingFor(CuratedSlice slice)
        => slice.Command == slice.Name || slice.ReadModels.Contains(slice.Name)
            ? $"SliceType = typeof({slice.Name})"
            : $"SliceName = \"{slice.Name}\"";

    private static string projected(
        CuratedModelFile model, string ns, string typeName, IReadOnlyList<ResolvedSpecOwnership> entries,
        IReadOnlyList<CuratedSlice> slices, IReadOnlyList<SlicePlan> plans)
    {
        var writer = new StringBuilder();
        var single = entries.Count == 1;

        writer.AppendLine("using Bobcat;");
        writer.AppendLine("using Xunit;");
        foreach (var each in usingsFor(model, slices)) writer.AppendLine($"using {each};");
        writer.AppendLine();
        writer.AppendLine($"namespace {ns};");
        writer.AppendLine();
        writer.AppendLine("/// <summary>");
        writer.AppendLine($"/// Projected specifications for {string.Join(", ", slices.Select(x => x.Name))}.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine("/// <remarks>");
        writer.AppendLine("/// Steps render from the marker comments in each test body; the verdict comes from the");
        writer.AppendLine("/// runner. [BobcatSlice] carries the BINDING only — the slice's domain, chapter and pattern");
        writer.AppendLine("/// are stated once, on the event model, and merge in by slice name.");
        writer.AppendLine("/// </remarks>");
        writer.AppendLine($"[BobcatFeature(\"{featureOf(slices[0])}\")]");

        // An integration slice's specs go through the store, and nothing here can know what this
        // repository boots one with — the same refusal to guess a base class that the code-first
        // skeleton makes. What the scaffolder owes is everything it DOES know: the binding, the
        // exact method names, and the steps.
        if (entries.Any(x => x.Kind == SpecKind.Integration))
        {
            writer.AppendLine("// TODO — these are integration slices: give this class the store. Derive from (or");
            writer.AppendLine("// inject) this repository's host/store fixture; the arrange/act/assert helpers are in");
            writer.AppendLine("// Bobcat.CritterStack. A unit-tested slice needs none of that — see the model's");
            writer.AppendLine("// spec-ownership manifest for which slices are which.");
        }
        if (single) writer.AppendLine($"[BobcatSlice({BindingFor(slices[0])})]");
        writer.AppendLine($"public class {typeName}");
        writer.AppendLine("{");

        var first = true;
        for (var i = 0; i < entries.Count; i++)
        {
            var slice = slices[i];
            foreach (var scenario in slice.Specifications?.Scenarios ?? [])
            {
                if (!first) writer.AppendLine();
                first = false;

                writer.AppendLine("    [Fact]");
                if (!single) writer.AppendLine($"    [BobcatSlice({BindingFor(slice)})]");

                if (!RoundTrips(scenario.Name))
                {
                    writer.AppendLine($"    // The model's scenario is \"{scenario.Name}\". A projected test's identity IS");
                    writer.AppendLine("    // its method name, so this one publishes a DIFFERENT identity and joins nothing.");
                    writer.AppendLine("    // Rename the scenario in the model to something a method name can spell.");
                }

                writer.AppendLine($"    public void {MethodNameFor(scenario.Name)}()");
                writer.AppendLine("    {");
                foreach (var line in StepSentences(plans[i], scenario)) writer.AppendLine($"        // {line}");
                writer.AppendLine();
                writer.AppendLine($"        throw new NotImplementedException(\"{slice.Name}: {scenario.Name}\");");
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
        return writer.ToString();
    }

    private static string codeFirst(
        CuratedModelFile model, string ns, string typeName, IReadOnlyList<ResolvedSpecOwnership> entries,
        IReadOnlyList<CuratedSlice> slices, IReadOnlyList<SlicePlan> plans)
    {
        var writer = new StringBuilder();

        writer.AppendLine("using Bobcat;");
        writer.AppendLine("using Bobcat.CodeFirst;");
        foreach (var each in usingsFor(model, slices)) writer.AppendLine($"using {each};");
        writer.AppendLine();
        writer.AppendLine($"namespace {ns};");
        writer.AppendLine();
        writer.AppendLine("/// <summary>");
        writer.AppendLine($"/// Code-first specifications for {string.Join(", ", slices.Select(x => x.Name))}.");
        writer.AppendLine("/// </summary>");
        writer.AppendLine($"[FixtureTitle(\"{featureOf(slices[0])}\")]");
        writer.AppendLine("// TODO — derive from this repository's CritterStack specification base, the one that");
        writer.AppendLine("// carries the store vocabulary (GivenStream/WhenCommand/ThenNewEvents). Specification");
        writer.AppendLine("// alone declares scenarios but binds no store.");
        writer.AppendLine($"public class {typeName} : Specification");
        writer.AppendLine("{");

        var first = true;
        for (var i = 0; i < entries.Count; i++)
        {
            var slice = slices[i];
            var tags = new List<string> { $"\"slice:{slice.Name}\"" };
            if (slice.Pattern is { Length: > 0 } pattern) tags.Add($"\"pattern:{pattern}\"");
            if (slice.Chapter is { Length: > 0 } chapter) tags.Add($"\"chapter:{chapter}\"");
            if (slice.Domain is { Length: > 0 } domain) tags.Add($"\"domain:{domain}\"");

            foreach (var scenario in slice.Specifications?.Scenarios ?? [])
            {
                if (!first) writer.AppendLine();
                first = false;

                writer.AppendLine($"    [Scenario(\"{scenario.Name}\", Tags = [{string.Join(", ", tags)}])]");
                writer.AppendLine($"    public void {MethodNameFor(scenario.Name)}()");
                writer.AppendLine("    {");
                writer.AppendLine("        // Declare the steps below and delete the throw — the shape is:");
                foreach (var line in StepSentences(plans[i], scenario)) writer.AppendLine($"        //   {line}");
                writer.AppendLine($"        throw new NotImplementedException(\"{slice.Name}: {scenario.Name}\");");
                writer.AppendLine("    }");
            }
        }

        writer.AppendLine("}");
        return writer.ToString();
    }

    private static string featureOf(CuratedSlice slice) => slice.Specifications?.Feature ?? slice.Name;

    /// <summary>
    /// The <c>using</c>s a skeleton needs for the types it names.
    /// </summary>
    /// <remarks>
    /// <c>SliceType = typeof(ConfirmAppointment)</c> is a type reference, and the slice's code is
    /// emitted into <c>{namespace}.{domain}</c> — a different namespace from the specs' own. Without
    /// the using the skeleton does not compile, which breaks the rule that a scaffold always
    /// compiles (issue #226): one unresolvable binding fails the whole spec project, so no slice's
    /// specs can run until every one of them is fixed by hand.
    /// </remarks>
    private static IEnumerable<string> usingsFor(CuratedModelFile model, IReadOnlyList<CuratedSlice> slices)
        => slices
            .Where(x => BindingFor(x).StartsWith("SliceType", StringComparison.Ordinal))
            .Select(x => $"{model.Namespace ?? model.Model}.{x.Domain ?? "Shared"}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>
    /// A scenario's given/when/then as one sentence each. Values ride inline rather than in a
    /// table, because neither a marker comment nor a code-first declaration has a table to put
    /// them in — and a table flattened into prose across several comment lines would read as
    /// several steps.
    /// </summary>
    public static IEnumerable<string> StepSentences(SlicePlan plan, CuratedScenario scenario)
    {
        foreach (var given in scenario.Given)
        {
            var where = given.Aggregate is { Length: > 0 } aggregate ? $" on {aggregate}" : "";
            yield return $"Given {given.Event}{where}{values(given.With)}";
        }

        // Through the PLAN, never off the curated `when:` directly. An automation's curated act
        // names the slice, but the code it describes takes the TRIGGER EVENT off the bus — and a
        // collapsed endpoint takes a POST at a route. Reading the model raw here made the projected
        // lane describe the same act differently from the .feature the Gherkin lane writes for it,
        // which is issue #231's defect in a new place: two halves deriving one decision separately.
        if (scenario.When is { } when) yield return $"{plan.ActStep}{values(when.With)}";

        foreach (var then in scenario.Then)
        {
            if (then.Event is { } emitted) yield return $"Then {emitted} is emitted{values(then.With)}";
            else if (then.ReadModel is { } readModel) yield return $"Then the {readModel} read model contains{values(then.Contains)}";
            else if (then.ValidationFails is { } reason)
            {
                // Same rule, and not interchangeable: a bus-dispatched command refuses by throwing,
                // a collapsed endpoint refuses with ProblemDetails and a 400.
                foreach (var step in plan.RefusalSteps(reason)) yield return step;
            }
            else if (then.RefusedWith is { } refusal)
            {
                // The status the model stated (issue #337). Missing this arm did not degrade the
                // step — it removed the whole `then:`, leaving a skeleton whose only step is the
                // act. In THIS lane the marker comments are the specification, so that reads as a
                // finished scenario rather than a missing one (issue #344).
                foreach (var step in plan.RefusalSteps(refusal.Reason ?? "refused", refusal.Status))
                {
                    yield return step;
                }
            }
        }
    }

    private static string values(Dictionary<string, string> with)
        => with.Count == 0 ? "" : $" ({string.Join(", ", with.Select(x => $"{x.Key} = {x.Value}"))})";
}
