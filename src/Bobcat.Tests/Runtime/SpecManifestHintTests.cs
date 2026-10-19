using System.Text.Json;
using Bobcat.Resilience;
using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Tests.Runtime;

/// <summary>
/// Issue #469: the recovery hints a suite declares on itself, carried across the process boundary
/// in its spec manifest so a supervisor that never loads the assembly can still honour them.
/// </summary>
public class SpecManifestHintTests
{
    // --- Fixtures. Read by passing their types explicitly, so nothing here stamps a hint on the
    //     rest of this test assembly.

    [NeverRecovers(typeof(SystemException), Because = "nothing in this class recovers")]
    private class HintedTests
    {
        public void ordinary_test() { }

        [ClearsInFreshProcess(typeof(Exception), Because = "this one leaks a static")]
        public void fresh_process_test() { }

        [ClearsOnRecycle("rabbit, postgres", typeof(TimeoutException))]
        public void recycled_test() { }
    }

    private class UnhintedTests
    {
        public void plain_test() { }
    }

    private abstract class HintedBase
    {
        [ClearsOnRetry(typeof(IOException))]
        public void inherited_test() { }
    }

    private class InheritingTests : HintedBase;

    [ClearsOnRetry(typeof(string))]
    private class MisdeclaredTests
    {
        [ClearsOnRecycle("", typeof(TimeoutException))]
        public void recycles_nothing() { }
    }

    private static readonly RecoveryHintAttribute[] assemblyDefaults =
    [
        new ClearsOnRetryAttribute(typeof(TimeoutException)) { Because = "the run-wide default" }
    ];

    private static IReadOnlyList<SpecManifestHint> read(params Type[] types)
        => SpecManifestHint.ReadFrom("Orders.Tests", assemblyDefaults, types);

    private static SpecManifest manifestWith(IReadOnlyList<SpecManifestHint> hints) => new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Orders.Tests",
        [new SpecManifestEntry("Orders/places an order", "Orders.Tests.OrderSpecs", "places_an_order")],
        hints);

    private static string classOf<T>() => typeof(T).FullName!;

    // --- Reading.

    [Fact]
    public void hints_are_read_at_assembly_class_and_method_scope()
    {
        var hints = read(typeof(HintedTests));

        hints.Select(h => (h.Scope, h.Kind, h.Source)).ShouldBe([
            (HintScope.Global, DispositionKind.RetryInProcess, "Orders.Tests"),
            (HintScope.Group, DispositionKind.FailAndContinue, nameof(HintedTests)),
            (HintScope.Test, DispositionKind.RetryInFreshProcess, $"{nameof(HintedTests)}.fresh_process_test"),
            (HintScope.Test, DispositionKind.RetryAfterRecycle, $"{nameof(HintedTests)}.recycled_test")
        ]);

        hints[0].TestClass.ShouldBeNull();
        hints[1].TestClass.ShouldBe(classOf<HintedTests>());
        hints[1].TestMethod.ShouldBeNull();
        hints[2].TestClass.ShouldBe(classOf<HintedTests>());
        hints[2].TestMethod.ShouldBe("fresh_process_test");
        hints[3].Resources.ShouldBe(["rabbit", "postgres"]);
    }

    [Fact]
    public void a_method_hint_on_an_inherited_test_is_reported_under_the_class_that_runs_it()
    {
        // A framework reports an inherited test under the concrete class, so that is the class the
        // supervisor will have to join the hint to. The abstract base itself runs nothing.
        var hint = read(typeof(HintedBase), typeof(InheritingTests))
            .Where(h => h.Scope == HintScope.Test).ShouldHaveSingleItem();

        hint.TestClass.ShouldBe(classOf<InheritingTests>());
        hint.TestMethod.ShouldBe("inherited_test");
    }

    [Fact]
    public void a_misdeclared_hint_is_reported_and_left_out_rather_than_costing_the_listing()
    {
        var warnings = new List<string>();

        var hints = SpecManifestHint.ReadFrom("Orders.Tests", assemblyDefaults,
            [typeof(MisdeclaredTests), typeof(HintedTests)], warnings.Add);

        // Both of MisdeclaredTests' hints are refused — one names a non-exception, the other
        // recycles nothing — and every well-formed hint still arrives.
        hints.ShouldNotContain(h => h.TestClass == classOf<MisdeclaredTests>());
        hints.Count.ShouldBe(4);
        warnings.Count.ShouldBe(2);
        warnings.ShouldContain(w => w.Contains("not an exception type"));
        warnings.ShouldContain(w => w.Contains("names no resources"));
    }

    // --- The document.

    [Fact]
    public void hints_at_all_three_scopes_survive_the_round_trip()
    {
        var written = read(typeof(HintedTests));

        var json = manifestWith(written).ToJson();
        var hints = SpecManifest.FromJson(json).ShouldNotBeNull().RecoveryHints.ShouldNotBeNull();

        hints.Count.ShouldBe(written.Count);
        for (var i = 0; i < written.Count; i++)
        {
            hints[i].FailureType.ShouldBe(written[i].FailureType);
            hints[i].Kind.ShouldBe(written[i].Kind);
            hints[i].Scope.ShouldBe(written[i].Scope);
            hints[i].Source.ShouldBe(written[i].Source);
            hints[i].Because.ShouldBe(written[i].Because);
            hints[i].TestClass.ShouldBe(written[i].TestClass);
            hints[i].TestMethod.ShouldBe(written[i].TestMethod);
            (hints[i].Resources ?? []).ShouldBe(written[i].Resources ?? []);
        }
    }

    [Fact]
    public void kinds_and_scopes_are_written_by_name()
    {
        // People read this file while triaging, and an enum's ordinal is nobody's contract.
        var json = manifestWith(read(typeof(HintedTests))).ToJson();

        json.ShouldContain("\"recoveryHints\"");
        json.ShouldContain("\"kind\": \"RetryAfterRecycle\"");
        json.ShouldContain("\"scope\": \"Group\"");
        json.ShouldContain("\"failureType\": \"System.TimeoutException\"");
    }

    [Fact]
    public void a_suite_that_declares_nothing_writes_no_hints_field_and_reads_back_as_none()
    {
        var manifest = manifestWith(null!) with { RecoveryHints = null };

        manifest.ToJson().ShouldNotContain("recoveryHints");
        SpecManifest.FromJson(manifest.ToJson())!.RecoveryHints.ShouldBeNull();
    }

    private sealed record ManifestBeforeHints(
        string Lane, string Framework, string Suite, IReadOnlyList<SpecManifestEntry> Specs);

    [Fact]
    public void a_reader_that_predates_the_field_still_reads_the_manifest()
    {
        // The field is additive: a resident runner or console built before #469 ignores it.
        var json = manifestWith(read(typeof(HintedTests))).ToJson();

        var old = JsonSerializer.Deserialize<ManifestBeforeHints>(
            json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        old.Suite.ShouldBe("Orders.Tests");
        old.Specs.ShouldHaveSingleItem().TestMethod.ShouldBe("places_an_order");
    }

    // --- The scope rule, as a supervisor applies it to what the manifest carried.

    private static RecoveryHintSet asSupervisorSees(IEnumerable<SpecManifestHint> hints,
        params (string Uid, string TestClass, string TestMethod)[] discovered)
    {
        var set = new RecoveryHintSet();
        foreach (var hint in hints)
        {
            var uids = discovered.Where(t => hint.Covers(t.TestClass, t.TestMethod)).Select(t => t.Uid).ToList();
            set.Add(hint.ToRecoveryHint(uids));
        }

        return set;
    }

    [Fact]
    public void the_narrowest_declaration_wins_even_when_it_names_a_less_specific_type()
    {
        var hints = SpecManifest.FromJson(manifestWith(read(typeof(HintedTests), typeof(UnhintedTests))).ToJson())!
            .RecoveryHints!;

        var set = asSupervisorSees(hints,
            ("uid-ordinary", classOf<HintedTests>(), "ordinary_test"),
            ("uid-fresh", classOf<HintedTests>(), "fresh_process_test"),
            ("uid-plain", classOf<UnhintedTests>(), "plain_test"));

        // In process, where the failure carries its whole inheritance chain.
        var timeout = FailureSignature.FromException(new TimeoutException());

        // The assembly says a timeout clears on retry; the class says nothing in it recovers, and
        // says it of SystemException — a less specific type than the assembly named. The class is
        // closer to the test, so the class wins without having to know what the default named.
        set.Best("uid-ordinary", timeout)!.Kind.ShouldBe(DispositionKind.FailAndContinue);

        // And a method beats its class, naming plain Exception — less specific again.
        set.Best("uid-fresh", timeout)!.Kind.ShouldBe(DispositionKind.RetryInFreshProcess);

        // A class that declares nothing falls back to the run-wide default.
        set.Best("uid-plain", timeout)!.Kind.ShouldBe(DispositionKind.RetryInProcess);
    }

    [Fact]
    public void from_the_wire_a_base_class_hint_abstains_and_the_wider_exact_hint_speaks()
    {
        // The documented degradation: a reported failure carries one type name, so the class's
        // SystemException hint cannot match a TimeoutException there. It abstains, and the
        // assembly default — which names the exact type — is what applies. A missing override,
        // never a wrong one invented.
        var set = asSupervisorSees(read(typeof(HintedTests)),
            ("uid-ordinary", classOf<HintedTests>(), "ordinary_test"));

        var reported = FailureSignature.FromReportedType("System.TimeoutException", "timed out");

        set.Best("uid-ordinary", reported)!.Scope.ShouldBe(HintScope.Global);
    }

    [Fact]
    public void a_class_hint_reaches_only_the_uids_of_that_class()
    {
        var set = asSupervisorSees(read(typeof(HintedTests)),
            ("uid-ordinary", classOf<HintedTests>(), "ordinary_test"),
            ("uid-elsewhere", "Orders.Tests.Elsewhere", "ordinary_test"));

        var timeout = FailureSignature.FromException(new TimeoutException());

        set.Best("uid-ordinary", timeout)!.Scope.ShouldBe(HintScope.Group);
        set.Best("uid-elsewhere", timeout)!.Scope.ShouldBe(HintScope.Global);
    }

    [Fact]
    public void a_nested_class_matches_whether_it_is_spelled_with_a_plus_or_a_dot()
    {
        var hint = read(typeof(HintedTests)).First(h => h.Scope == HintScope.Group);

        hint.TestClass!.ShouldContain("+");
        hint.Covers(hint.TestClass, "anything").ShouldBeTrue();
        hint.Covers(hint.TestClass.Replace('+', '.'), "anything").ShouldBeTrue();
        hint.Covers("Some.Other.Class", "anything").ShouldBeFalse();
    }

    [Fact]
    public void a_declaration_that_reaches_no_discovered_test_applies_to_nothing()
    {
        // Not to everything: an empty uid set is a hint whose class discovery never reported.
        var hint = read(typeof(HintedTests)).First(h => h.Scope == HintScope.Group).ToRecoveryHint([]);

        hint.AppliesTo("uid-anything").ShouldBeFalse();
    }
}
