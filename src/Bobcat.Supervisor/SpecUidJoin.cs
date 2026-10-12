using Bobcat.Runtime;

namespace Bobcat.Supervisor;

/// <summary>
/// The join between what a suite says it specifies — its <see cref="SpecManifest"/>, keyed by
/// <c>{Feature}/{Scenario}</c> — and what the test platform runs, keyed by uid.
/// </summary>
/// <remarks>
/// <para>
/// <b>One copy of the rule, two directions.</b> Running a named specification needs identity →
/// uids (<see cref="UidsByIdentity"/>, the resident runner's warm path); reporting one needs uid →
/// identity (<see cref="IdentityByUid"/>, what the supervisor stamps on <c>test_started</c> /
/// <c>test_finished</c> for issue #515). They are the same lookup read from either end, and two
/// copies of it is how one end would silently start naming a different test than the other.
/// </para>
/// <para>
/// <b>The lookup itself is issue #394's finding.</b> xUnit v3 reports a test's discovery display
/// name as <c>Namespace.Class.method</c>, which is exactly what the manifest's <c>TestClass</c> +
/// <c>TestMethod</c> spell. A nested class is <c>Ns.Outer+Inner</c> in the manifest, because that
/// is what a framework's class filter takes, while a display name may spell it with a dot — tried
/// as a fallback rather than instead, since the exact match is the one #394 verified. TUnit
/// (bobcat#470) joins on neither: its display name is the bare method name, so its reported
/// <c>location.type</c> / <c>location.method</c> are the match, and a data-driven method is one
/// identity at several uids.
/// </para>
/// </remarks>
internal static class SpecUidJoin
{
    /// <summary>Every specification identity mapped to the platform uids that run it.</summary>
    public static Dictionary<string, IReadOnlyList<string>> UidsByIdentity(
        SpecManifest manifest, IReadOnlyList<WorkerTest> discovered)
    {
        var byDisplayName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var test in discovered) byDisplayName[test.DisplayName] = test.Uid;

        var byLocation = discovered
            .Where(test => test.TestClass is { Length: > 0 } && test.TestMethod is { Length: > 0 })
            .GroupBy(test => $"{test.TestClass}.{test.TestMethod}", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(test => test.Uid).ToList(),
                StringComparer.Ordinal);

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var entry in manifest.Specs)
        {
            if (entry.QualifiedTestMethod is not { Length: > 0 } qualified) continue;

            if (byDisplayName.TryGetValue(qualified, out var uid))
            {
                map[entry.Identity] = [uid];
                continue;
            }

            if (byDisplayName.TryGetValue(qualified.Replace('+', '.'), out uid))
            {
                map[entry.Identity] = [uid];
                continue;
            }

            if (byLocation.TryGetValue(qualified, out var located)) map[entry.Identity] = located;
        }

        return map;
    }

    /// <summary>
    /// Every platform uid mapped to the specification identity it runs — the inverse of
    /// <see cref="UidsByIdentity"/>. Empty when there is no manifest, which is also what a suite
    /// that is not a Bobcat spec suite produces.
    /// </summary>
    /// <remarks>
    /// <b>An identity more than one test claims is left out</b> (bobcat#489 calls it a duplicate
    /// and the manifest reports it): a manifest keeps one entry per identity, so stamping it would
    /// name whichever claimant bound first and leave the others unnamed — an identity that cannot
    /// pick out one test is not an identifier, and a fold keyed on it would attach a scenario to
    /// the wrong test. <see cref="SpecFilterArguments"/> refuses such an identity for the same
    /// reason.
    /// </remarks>
    public static Dictionary<string, string> IdentityByUid(
        SpecManifest? manifest, IReadOnlyList<WorkerTest> discovered)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifest is null) return map;

        foreach (var (identity, uids) in UidsByIdentity(manifest, discovered))
        {
            if (manifest.IsAmbiguous(identity)) continue;

            foreach (var uid in uids) map[uid] = identity;
        }

        return map;
    }
}
