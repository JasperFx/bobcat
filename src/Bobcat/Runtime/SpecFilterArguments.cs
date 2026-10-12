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
/// <b>TUnit is deliberately unsupported for now.</b> Its filter is a tree-node path rather than a
/// method name, and this repository cannot run a TUnit host to verify the spelling against:
/// <c>TUnit.Engine</c> needs Microsoft.Testing.Platform 2.4.0 and src is pinned to 1.9.1 (see
/// <c>Bobcat.Xunit.Tests.csproj</c>, where the TUnit adapter is tested without a TUnit runner for
/// that reason). An unverified filter string would be exactly the silent whole-suite run above, so
/// it says so instead.
/// </para>
/// </remarks>
public static class SpecFilterArguments
{
    /// <summary>xUnit v3's option: one or more fully qualified method names, ORed.</summary>
    public const string MethodOption = "--filter-method";

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

        switch (manifest.Framework)
        {
            case SpecManifest.XunitFramework:
                return [MethodOption, .. selection.Identities.Select(identity => qualifiedMethod(manifest, identity))];

            case SpecManifest.TUnitFramework:
                throw new NotSupportedException(
                    "Running a TUnit suite by specification identity is not supported yet: TUnit filters by "
                    + "tree-node path rather than by method name, and the spelling is not verified anywhere in "
                    + "this repository (TUnit.Engine needs Microsoft.Testing.Platform 2.4.0, and src is pinned "
                    + "to 1.9.1). An unverified filter would run the whole suite while looking filtered.");

            // Both run on Microsoft.Testing.Platform 1.9 here, so unlike TUnit their filter spelling
            // CAN be verified in this repository — it just has not been yet (issues #385/#386 shipped
            // the adapters, not run-by-identity). Refused until a test pins the spelling against a
            // real host, as ProjectedSpecIdentityEndToEndTests does for xUnit.
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

    private static string qualifiedMethod(SpecManifest manifest, string identity)
        => manifest.For(identity)?.QualifiedTestMethod
           ?? throw new NotSupportedException(
               $"'{identity}' is in {manifest.Suite}'s manifest but carries no test method, so there is nothing "
               + "to hand a method filter. A projected suite's manifest is written by the generated registration, "
               + "which binds every identity it declares — an entry without one means the manifest was written by "
               + "something else.");
}
