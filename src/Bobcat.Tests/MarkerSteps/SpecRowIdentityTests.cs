using System.Reflection;
using Bobcat.Monitoring;
using Bobcat.Runtime;
using Shouldly;
using GeneratorNaming = Bobcat.Generators.MarkerSpecNaming;

namespace Bobcat.Tests.MarkerSteps;

/// <summary>
/// bobcat#569: each row of a data-driven specification is its own specification at run time, under
/// the method's identity plus the row, and names the method's identity as its parent on the wire.
/// </summary>
[Collection("marker-step-run")]
public class SpecRowIdentityTests : IDisposable
{
    private const string ParentUid = "Discounts/discount is applied";

    private readonly RecordingSink _sink = new();

    public SpecRowIdentityTests() => MarkerStepRun.Reset(_sink);

    public void Dispose()
    {
        MarkerStepRun.Reset();
        DeclaredSteps.Remove(ParentUid);
    }

    private static MethodInfo method => typeof(DiscountSpecs).GetMethod(nameof(DiscountSpecs.discount_is_applied))!;

    private static void start()
        => MarkerStepRun.StartForTesting(new MonitorRunInfo(Guid.NewGuid(), "Suite", "/repo", "main", "xunit"));

    // --- The row part of an identity

    [Fact]
    public void a_row_is_named_by_its_arguments()
        => SpecRows.Describe(["amount", "tier"], [100, Tier.Gold], null).ShouldBe("(amount: 100, tier: Gold)");

    [Fact]
    public void a_label_names_the_row_instead()
        => SpecRows.Describe(["amount", "tier"], [100, Tier.Gold], "gold customer").ShouldBe("[gold customer]");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void a_blank_label_is_no_label(string? label)
        => SpecRows.Describe(["amount"], [100], label).ShouldBe("(amount: 100)");

    [Fact]
    public void a_test_with_no_row_has_no_row_part()
    {
        SpecRows.Describe([], null, null).ShouldBeNull();
        SpecRows.Describe([], [], null).ShouldBeNull();
    }

    [Fact]
    public void a_value_with_no_parameter_to_name_it_is_named_by_position()
        => SpecRows.Describe(["first"], [1, 2], null).ShouldBe("(first: 1, arg1: 2)");

    [Fact]
    public void values_are_formatted_with_the_invariant_culture_whatever_the_current_one_is()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            SpecRows.Format(1.5d).ShouldBe("1.5");
            SpecRows.Format(2.25m).ShouldBe("2.25");
            SpecRows.Format(1234567).ShouldBe("1234567");
            SpecRows.Format(new DateOnly(2026, 10, 16)).ShouldBe("10/16/2026");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void values_read_as_a_spec_author_wrote_them()
    {
        SpecRows.Format(null).ShouldBe("null");
        SpecRows.Format("Gold").ShouldBe("Gold");
        SpecRows.Format(true).ShouldBe("true");
        SpecRows.Format('x').ShouldBe("x");
        SpecRows.Format(Tier.Silver).ShouldBe("Silver");
        SpecRows.Format(typeof(DiscountSpecs)).ShouldBe("DiscountSpecs");
        SpecRows.Format(new[] { 1, 2 }).ShouldBe("[1, 2]");

        // One line, always: an identity is a key, and a line break in one breaks every line-based reader
        SpecRows.Format("two\nlines").ShouldBe("two\\nlines");
    }

    // --- The generator's copy, which lists constant rows in the manifest, must agree

    [Theory]
    [InlineData("gold customer")]
    [InlineData("  padded  ")]
    [InlineData("two\r\nlines")]
    public void the_generator_labels_a_row_the_same_way(string label)
        => GeneratorNaming.RowLabel(label).ShouldBe(SpecRows.Labelled(label));

    [Fact]
    public void the_generator_names_a_row_by_its_arguments_the_same_way()
    {
        string[] names = ["amount", "tier"];
        string[] values = ["100", "Gold", "extra"];

        GeneratorNaming.RowArguments(names, values).ShouldBe(SpecRows.Arguments(names, values));
        GeneratorNaming.RowTitle("discount is applied", "(amount: 100)")
            .ShouldBe(SpecRows.Title("discount is applied", "(amount: 100)"));
        GeneratorNaming.EscapeRowValue("a\nb\rc").ShouldBe(SpecRows.Format("a\nb\rc"));
    }

    // --- The recording

    [Fact]
    public void a_row_records_under_its_own_identity_and_names_its_parent()
    {
        start();

        var recording = MarkerStepRun.BeginScenario(method, "xunit", [100, Tier.Gold], null);

        recording.Uid.ShouldBe("Discounts/discount is applied (amount: 100, tier: Gold)");
        recording.Scenario.ShouldBe("discount is applied (amount: 100, tier: Gold)");
        recording.Row.ShouldBe("(amount: 100, tier: Gold)");
        recording.ParentUid.ShouldBe(ParentUid);
        recording.SpecUid.ShouldBe(ParentUid);
    }

    [Fact]
    public void a_test_with_no_row_is_exactly_the_scenario_it_always_was()
    {
        start();

        var recording = MarkerStepRun.BeginScenario(method, "xunit", null, null);

        recording.Uid.ShouldBe(ParentUid);
        recording.ParentUid.ShouldBeNull();
        recording.SpecUid.ShouldBe(ParentUid);
    }

    [Fact]
    public void the_type_and_name_overload_names_a_row_the_same_way()
    {
        // TUnit, NUnit and MSTest hand over no MethodInfo: the parameter names come from the method
        // of that name, and the identity has to be the one xUnit's shape mints
        start();

        var recording = MarkerStepRun.BeginScenario(
            typeof(DiscountSpecs), nameof(DiscountSpecs.discount_is_applied), "tunit", [100, Tier.Gold], null);

        recording.Uid.ShouldBe("Discounts/discount is applied (amount: 100, tier: Gold)");
        recording.ParentUid.ShouldBe(ParentUid);
    }

    [Fact]
    public void every_row_renders_the_steps_its_method_declares()
    {
        // The rows run one body, and the generator registers its marker comments once, under the
        // method's identity
        DeclaredSteps.Register(ParentUid, new DeclaredStep("Given", "a gold customer", 12));
        start();

        var recording = MarkerStepRun.BeginScenario(method, "xunit", [100, Tier.Gold], "gold");

        recording.Uid.ShouldBe("Discounts/discount is applied [gold]");
        recording.Declared.ShouldHaveSingleItem().Text.ShouldBe("a gold customer");
    }

    [Fact]
    public void a_rows_events_carry_its_own_uid_and_its_parent()
    {
        start();

        MarkerStepRun.BeginScenario(method, "xunit", [50, Tier.Silver], null);
        MarkerStepRun.EndScenario(ScenarioVerdict.Passed);

        var started = _sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem();
        started.Uid.ShouldBe("Discounts/discount is applied (amount: 50, tier: Silver)");
        started.Scenario.ShouldBe("discount is applied (amount: 50, tier: Silver)");
        started.ParentSpecId.ShouldBe(ParentUid);

        var finished = _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem();
        finished.Uid.ShouldBe("Discounts/discount is applied (amount: 50, tier: Silver)");
        finished.ParentSpecId.ShouldBe(ParentUid);
    }

    [Fact]
    public void a_scenario_that_is_not_a_row_sends_no_parent()
    {
        start();

        MarkerStepRun.BeginScenario(method, "xunit");
        MarkerStepRun.EndScenario(ScenarioVerdict.Passed);

        _sink.Events.OfType<ScenarioStarted>().ShouldHaveSingleItem().ParentSpecId.ShouldBeNull();
        _sink.Events.OfType<ScenarioFinished>().ShouldHaveSingleItem().ParentSpecId.ShouldBeNull();
    }

    // --- Running by identity

    private static SpecManifest manifest(string framework = SpecManifest.XunitFramework) => new(
        SpecManifest.ProjectedLane, framework, "Discounts.Tests",
        [
            new SpecManifestEntry(ParentUid, "Discounts.Tests.DiscountSpecs", "discount_is_applied"),
            new SpecManifestEntry(ParentUid + " (amount: 100)", "Discounts.Tests.DiscountSpecs", "discount_is_applied", ParentUid),
            new SpecManifestEntry(ParentUid + " (amount: 50)", "Discounts.Tests.DiscountSpecs", "discount_is_applied", ParentUid)
        ]);

    [Fact]
    public void one_row_cannot_be_run_by_a_method_filter_and_is_refused_naming_the_method()
    {
        // A method filter runs every row: a request for one row that ran them all would look filtered
        var refusal = Should.Throw<NotSupportedException>(() =>
            SpecFilterArguments.For(manifest(), SpecSelection.Of(ParentUid + " (amount: 100)")));

        refusal.Message.ShouldContain("'Discounts/discount is applied (amount: 100)'");
        refusal.Message.ShouldContain("Run the whole specification instead: 'Discounts/discount is applied'");
    }

    [Fact]
    public void the_method_identity_runs_every_row()
        => SpecFilterArguments.For(manifest(), SpecSelection.Of(ParentUid))
            .ShouldBe(["--filter-method", "Discounts.Tests.DiscountSpecs.discount_is_applied"]);

    [Fact]
    public void a_row_asked_for_beside_its_method_is_already_covered()
        => SpecFilterArguments.For(manifest(), SpecSelection.Of(ParentUid, ParentUid + " (amount: 50)"))
            .ShouldBe(["--filter-method", "Discounts.Tests.DiscountSpecs.discount_is_applied"]);

    [Fact]
    public void a_row_is_listed_with_its_parent_and_the_method_is_listed_without_one()
    {
        var json = manifest().ToJson();

        json.ShouldContain("\"parent\": \"Discounts/discount is applied\"");
        SpecManifest.FromJson(json)!.For(ParentUid + " (amount: 50)")!.IsRow.ShouldBeTrue();
        SpecManifest.FromJson(json)!.For(ParentUid)!.IsRow.ShouldBeFalse();
    }

    [Fact]
    public void a_constant_row_binds_with_its_parent()
    {
        const string row = ParentUid + " [bound row]";
        try
        {
            DeclaredSteps.Bind(row, "Discounts.Tests.DiscountSpecs", "discount_is_applied", ParentUid);

            DeclaredSteps.BindingFor(row).ShouldBe(
                new SpecManifestEntry(row, "Discounts.Tests.DiscountSpecs", "discount_is_applied", ParentUid));
        }
        finally
        {
            DeclaredSteps.Remove(row);
        }
    }

    public enum Tier { Silver, Gold }

    [BobcatFeature("Discounts")]
    public class DiscountSpecs
    {
        public void discount_is_applied(int amount, Tier tier) { }
    }

    private sealed class RecordingSink : IMonitorEventSink
    {
        private readonly List<MonitorEvent> _events = new();

        public IReadOnlyList<MonitorEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public void Post(MonitorEvent @event)
        {
            lock (_events) _events.Add(@event);
        }
    }
}
