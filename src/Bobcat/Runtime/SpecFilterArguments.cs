namespace Bobcat.Runtime;

/// <summary>
/// Turns a <see cref="SpecSelection"/> into the command line the suite's own runner understands
/// (issue #391), so a monitor names identities and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The identity has to be translated.</b> A projected suite's uid is its test framework's, built from the assembly, class, method and arguments —
/// nothing a monitor could know or should have to — so the identity is translated to the method
/// the manifest bound it to and passed as that framework's method filter.
/// </para>
/// <para>
/// <b>It refuses rather than guesses.</b> An unknown framework, or an entry with no binding,
/// throws naming what is missing. The alternative is worse than an error: MTP ignores a filter
/// option it does not recognise and runs the whole suite, so a wrong spelling here is a run that
/// looks filtered and is not — the same failure <c>GuardAgainstAnUnfilteredRun</c> was written for.
/// </para>
/// <para>
/// <b>TUnit is filtered by tree-node path (bobcat#470), and only where one path is exact.</b>
/// Its filter is <c>--treenode-filter "/*/&lt;Namespace&gt;/&lt;Class&gt;/&lt;Method&gt;"</c>, measured
/// against a real TUnit host (<c>TUnitTreeNodeFilterTests</c>): the namespace is one segment with
/// its dots kept, a nested class is <c>Outer+Inner</c>, segments match whole words, and a
/// data-driven method's path selects every case. The platform accepts the option <b>once</b>, and
/// its OR works <b>within a segment only</b> — <c>/*/Ns/(A)|(B)/(x)|(y)</c> is legal, a whole-path
/// alternative is a parse error, and an unparenthesised <c>|</c> between two paths silently selects
/// something else. So several identities become one filter only when they are exactly the cross
/// product of their namespaces, classes and methods (one class, or one method name across several
/// classes); any other selection would run tests nobody asked for, and is refused.
/// </para>
/// </remarks>
public static class SpecFilterArguments
{
    /// <summary>xUnit v3's option: one or more fully qualified method names, ORed.</summary>
    public const string MethodOption = "--filter-method";

    /// <summary>
    /// Microsoft.Testing.Platform's tree-node filter, which TUnit uses: one path, accepted once.
    /// </summary>
    public const string TreeNodeOption = "--treenode-filter";

    /// <summary>
    /// Characters with a meaning in a tree-node filter. None can appear in a C# namespace, type or
    /// method name, so an entry carrying one was not written by the generator, and is refused
    /// rather than escaped into a spelling nobody has verified.
    /// </summary>
    private static readonly char[] treeNodeOperators = ['/', '(', ')', '|', '&', '!', '*', '[', ']', '=', '\\', ' '];

    /// <summary>
    /// The arguments that narrow <paramref name="manifest"/>'s suite to
    /// <paramref name="selection"/> — empty when the selection narrows nothing, because the
    /// unfiltered run needs no argument.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The selection names a specification the manifest does not have. Checked here rather than
    /// left to the runner so that no caller can build a filter for a spec that does not exist,
    /// which a framework would quietly match nothing for.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The suite's framework has no verified identity-to-filter translation.
    /// </exception>
    public static IReadOnlyList<string> For(SpecManifest manifest, SpecSelection selection)
    {
        if (!selection.NarrowsAnything) return [];

        var unknown = selection.NotIn(manifest.Identities);
        if (unknown.Count > 0)
        {
            throw new ArgumentException(
                $"'{manifest.Suite}' has no specification named {string.Join(", ", unknown.Select(x => $"'{x}'"))}. "
                + "A selection has to be checked against the suite's own manifest before it is turned into a "
                + "filter — a framework asked for a test it does not have simply runs nothing, which reads as "
                + "a passing run.",
                nameof(selection));
        }

        // bobcat#489. Two tests under one identity: the manifest kept one of them, and a filter built
        // from it would run that one while the request meant "this specification". Refused by name,
        // like an unknown identity, because guessing is the silent wrong run this class exists to stop.
        var ambiguous = selection.Identities.Where(manifest.IsAmbiguous).ToList();
        if (ambiguous.Count > 0)
        {
            throw new ArgumentException(
                $"'{manifest.Suite}' has more than one test claiming {string.Join(", ", ambiguous.Select(x => $"'{x}'"))}, "
                + "so there is no one specification to run by that identity. Rename one of the test methods, or give "
                + "one an explicit scenario name with [BobcatSpec(Name = \"...\")].",
                nameof(selection));
        }

        // bobcat#569. A row of a data-driven spec shares its method with every other row, so a method
        // filter cannot pick it out: it would run the whole theory while the request named one row.
        // Refused by name, pointing at the method's identity, unless that identity is asked for too —
        // then the row is already covered and drops out.
        var rows = selection.Identities.Where(identity => manifest.For(identity)?.IsRow == true).ToList();
        var lonelyRows = rows.Where(identity => !selection.Identities.Contains(manifest.For(identity)!.Parent!, SpecIdentity.Comparer)).ToList();
        if (lonelyRows.Count > 0)
        {
            throw new NotSupportedException(
                $"{string.Join(", ", lonelyRows.Select(x => $"'{x}'"))} "
                + (lonelyRows.Count == 1 ? "is one row" : "are rows")
                + " of a data-driven specification, and a framework filter selects a test method, not one of its "
                + "rows: it would run every row while looking filtered to one. Run the whole specification instead: "
                + string.Join(", ", lonelyRows.Select(x => $"'{manifest.For(x)!.Parent}'").Distinct()) + ".");
        }

        if (rows.Count > 0) selection = SpecSelection.Of(selection.Identities.Except(rows, SpecIdentity.Comparer));

        switch (manifest.Framework)
        {
            case SpecManifest.XunitFramework:
                return [MethodOption, .. selection.Identities.Select(identity => qualifiedMethod(manifest, identity))];

            case SpecManifest.TUnitFramework:
                return [TreeNodeOption, treeNodeFilter(manifest, selection)];

            // Their filter spelling CAN be verified in this repository — it just has not been yet
            // (issues #385/#386 shipped the adapters, not run-by-identity). Refused until a test pins
            // the spelling against a real host, as ProjectedSpecIdentityEndToEndTests does for xUnit
            // and TUnitResidentSuiteTests does for TUnit.
            case SpecManifest.NUnitFramework:
            case SpecManifest.MSTestFramework:
                throw new NotSupportedException(
                    $"Running {(manifest.Framework == SpecManifest.NUnitFramework ? "an NUnit" : "an MSTest")} suite "
                    + "by specification identity is not supported yet: the adapter records and publishes its "
                    + "specifications, but no filter spelling for it has been verified against a real test host. "
                    + "An unverified filter would run the whole suite while looking filtered.");

            default:
                throw new NotSupportedException(
                    $"'{manifest.Framework}' is not a framework Bobcat knows how to filter by specification "
                    + $"identity. Expected one of '{SpecManifest.XunitFramework}', "
                    + $"'{SpecManifest.TUnitFramework}', '{SpecManifest.NUnitFramework}' or '{SpecManifest.MSTestFramework}'.");
        }
    }

    /// <summary>
    /// The one tree-node path that runs exactly the selection, or a refusal saying why there is none.
    /// </summary>
    private static string treeNodeFilter(SpecManifest manifest, SpecSelection selection)
    {
        var tests = selection.Identities
            .Select(identity => (Identity: identity, Place: treeNodePlace(manifest, identity)))
            .ToList();

        var namespaces = tests.Select(x => x.Place.Namespace).Distinct(StringComparer.Ordinal).ToList();
        var classes = tests.Select(x => x.Place.Class).Distinct(StringComparer.Ordinal).ToList();
        var methods = tests.Select(x => x.Place.Method).Distinct(StringComparer.Ordinal).ToList();
        var distinct = tests.Select(x => x.Place).Distinct().Count();

        // The platform ORs within a segment and never across a whole path, so a filter of
        // per-segment alternatives selects their CROSS PRODUCT. That is exactly the selection only
        // when the selection already is that product; otherwise the extra combinations may be real
        // tests outside the request, and a run that quietly includes them is the run this class
        // exists to stop.
        if ((long)namespaces.Count * classes.Count * methods.Count != distinct)
        {
            throw new NotSupportedException(
                $"These specifications cannot be run together from one TUnit filter: "
                + $"{string.Join(", ", tests.Select(x => $"'{x.Identity}'"))}. TUnit is filtered by "
                + "Microsoft.Testing.Platform's --treenode-filter, which takes one path and ORs only within "
                + "a segment, so it can name several methods in one class or one method name across several "
                + "classes, but not an arbitrary set. Run them one class at a time, or as a warm command, "
                + "which runs by test id instead of by filter.");
        }

        return $"/*/{alternatives(namespaces)}/{alternatives(classes)}/{alternatives(methods)}";
    }

    private static string alternatives(IReadOnlyList<string> segment)
        => segment.Count == 1 ? segment[0] : string.Join("|", segment.Select(x => $"({x})"));

    private static (string Namespace, string Class, string Method) treeNodePlace(SpecManifest manifest, string identity)
    {
        // qualifiedMethod refuses an entry with no binding, in the words the xUnit path uses.
        var qualified = qualifiedMethod(manifest, identity);
        var entry = manifest.For(identity)!;
        var type = entry.TestClass!;
        var method = entry.TestMethod!;

        // The class is the last dotted part ("Ns.Outer+Inner" -> "Outer+Inner"): a CLR type name
        // carries no dots, and a nested class keeps its '+', which is how TUnit names the segment.
        var lastDot = type.LastIndexOf('.');

        // A class in the global namespace has no namespace segment to name, and how TUnit spells
        // that is not measured. Refused rather than guessed.
        if (lastDot <= 0)
        {
            throw new NotSupportedException(
                $"'{identity}' is declared on '{type}', which is in the global namespace. TUnit's tree-node "
                + "path has a namespace segment, and its spelling for the global namespace is not verified, "
                + "so there is no filter Bobcat can build for it. Put the test class in a namespace.");
        }

        var place = (type[..lastDot], type[(lastDot + 1)..], method);

        if (place.Item1.IndexOfAny(treeNodeOperators) >= 0
            || place.Item2.IndexOfAny(treeNodeOperators) >= 0
            || method.IndexOfAny(treeNodeOperators) >= 0)
        {
            throw new NotSupportedException(
                $"'{identity}' is bound to '{qualified}', which contains a character with a "
                + "meaning in a tree-node filter. No C# name can, so this manifest was not written by "
                + "Bobcat's generator, and no filter is built for it.");
        }

        return place;
    }

    private static string qualifiedMethod(SpecManifest manifest, string identity)
        => manifest.For(identity)?.QualifiedTestMethod
           ?? throw new NotSupportedException(
               $"'{identity}' is in {manifest.Suite}'s manifest but carries no test method, so there is nothing "
               + "to hand a method filter. A projected suite's manifest is written by the generated registration, "
               + "which binds every identity it declares — an entry without one means the manifest was written by "
               + "something else.");
}
