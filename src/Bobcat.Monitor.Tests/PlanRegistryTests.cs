using Bobcat.Monitor.Coordination;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class PlanRegistryTests : IDisposable
{
    private readonly string _plansPath =
        Path.Combine(Path.GetTempPath(), "bobcat-plan-registry-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_plansPath, recursive: true); }
        catch { }
    }

    private void writePlan(string fileName, string slug, string extra = "")
        => File.WriteAllText(Path.Combine(_plansPath, fileName),
            $"""
             schema: 1
             plan: {slug}
             {extra}
             nodes:
               - id: only
                 kind: test-run-gate
             """);

    private PlanRegistry newRegistry()
    {
        Directory.CreateDirectory(_plansPath);
        return new PlanRegistry(_plansPath);
    }

    private const string PushablePlan =
        """
        schema: 1
        plan: pushed-plan
        nodes:
          - id: only
            kind: test-run-gate
        """;

    [Fact]
    public void loads_the_plans_directory_at_construction()
    {
        Directory.CreateDirectory(_plansPath);
        writePlan("a.yaml", "plan-a");
        writePlan("b.yml", "plan-b", "title: Plan B");

        var registry = newRegistry();

        registry.All().Select(x => x.Slug).ShouldBe(["plan-a", "plan-b"]);
        var b = registry.Find("plan-b")!;
        b.Source.ShouldBe(PlanSource.File);
        b.Document!.Title.ShouldBe("Plan B");
        b.SourcePath.ShouldEndWith("b.yml");
    }

    [Fact]
    public void a_broken_file_registers_with_its_errors_instead_of_vanishing()
    {
        Directory.CreateDirectory(_plansPath);
        File.WriteAllText(Path.Combine(_plansPath, "broken.yaml"), "schema: 1\nplan: broken\nnodes: []\n");

        var registry = newRegistry();

        var entry = registry.All().Single();
        entry.IsValid.ShouldBeFalse();
        entry.Slug.ShouldBe("broken"); // keyed by filename stem when the document won't parse
        entry.Errors.ShouldContain(x => x.Contains("at least one node"));
    }

    [Fact]
    public void two_files_declaring_the_same_slug_register_the_second_as_an_error()
    {
        Directory.CreateDirectory(_plansPath);
        writePlan("first.yaml", "shared-slug");
        writePlan("second.yaml", "shared-slug");

        var registry = newRegistry();

        var winner = registry.Find("shared-slug")!;
        winner.IsValid.ShouldBeTrue();
        winner.SourcePath.ShouldEndWith("first.yaml"); // ordinal file order, deterministic

        var loser = registry.Find("second")!;
        loser.IsValid.ShouldBeFalse();
        loser.Errors.Single().ShouldContain("already declared by");
        loser.Errors.Single().ShouldContain("first.yaml");
    }

    [Fact]
    public void rescan_picks_up_new_files_and_drops_deleted_ones()
    {
        Directory.CreateDirectory(_plansPath);
        writePlan("a.yaml", "plan-a");
        var registry = newRegistry();

        File.Delete(Path.Combine(_plansPath, "a.yaml"));
        writePlan("b.yaml", "plan-b");

        var result = registry.Rescan();

        result.ShouldBe(new RescanResult(Valid: 1, Invalid: 0, Removed: 1));
        registry.Find("plan-a").ShouldBeNull();
        registry.Find("plan-b").ShouldNotBeNull();
    }

    [Fact]
    public void pushed_plans_survive_a_rescan()
    {
        var registry = newRegistry();
        registry.Push("pushed-plan", PushablePlan).Succeeded.ShouldBeTrue();

        registry.Rescan();

        var plan = registry.Find("pushed-plan")!;
        plan.Source.ShouldBe(PlanSource.Pushed);
    }

    [Fact]
    public void an_invalid_push_is_refused_with_its_errors()
    {
        var registry = newRegistry();

        var result = registry.Push("nope", "schema: 1\nplan: nope\nnodes: []\n");

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.Contains("at least one node"));
        registry.Find("nope").ShouldBeNull(); // nothing half-parsed registered
    }

    [Fact]
    public void a_push_must_agree_with_its_own_slug()
    {
        var registry = newRegistry();

        var result = registry.Push("other-slug", PushablePlan);

        result.Succeeded.ShouldBeFalse();
        result.Errors.Single().ShouldContain("declares plan 'pushed-plan' but was pushed to 'other-slug'");
    }

    [Fact]
    public void a_file_owns_its_slug_against_pushes()
    {
        Directory.CreateDirectory(_plansPath);
        writePlan("owned.yaml", "owned-plan");
        var registry = newRegistry();

        var result = registry.Push("owned-plan",
            """
            schema: 1
            plan: owned-plan
            nodes:
              - id: other
                kind: test-run-gate
            """);

        result.Succeeded.ShouldBeFalse();
        result.FileOwned.ShouldBeTrue();
        result.Errors.Single().ShouldContain("owned by file");
    }

    [Fact]
    public void a_file_takes_a_pushed_plans_slug_on_rescan()
    {
        var registry = newRegistry();
        registry.Push("pushed-plan", PushablePlan).Succeeded.ShouldBeTrue();

        writePlan("takeover.yaml", "pushed-plan");
        registry.Rescan();

        var plan = registry.Find("pushed-plan")!;
        plan.Source.ShouldBe(PlanSource.File); // the source-controlled artifact wins
        plan.SourcePath.ShouldEndWith("takeover.yaml");
    }

    [Fact]
    public void only_pushed_plans_can_be_removed()
    {
        Directory.CreateDirectory(_plansPath);
        writePlan("kept.yaml", "kept-plan");
        var registry = newRegistry();
        registry.Push("pushed-plan", PushablePlan);

        registry.Remove("pushed-plan").ShouldBe(RemovePlanResult.Removed);
        registry.Remove("pushed-plan").ShouldBe(RemovePlanResult.NotFound);
        registry.Remove("kept-plan").ShouldBe(RemovePlanResult.FileOwned);

        registry.Find("kept-plan").ShouldNotBeNull();
    }

    [Fact]
    public void a_broken_files_stem_key_never_steals_a_real_slug()
    {
        Directory.CreateDirectory(_plansPath);
        // a.yaml validly declares plan "collide"; collide.yaml is broken and would key by stem.
        writePlan("a.yaml", "collide");
        File.WriteAllText(Path.Combine(_plansPath, "collide.yaml"), "not: [valid");

        var registry = newRegistry();

        registry.Find("collide")!.IsValid.ShouldBeTrue();
        var broken = registry.All().Single(x => !x.IsValid);
        broken.Slug.ShouldBe("collide-2");
    }

    [Fact]
    public void views_render_wire_strings_not_enum_names()
    {
        var registry = newRegistry();
        registry.Push("pushed-plan",
            """
            schema: 1
            plan: pushed-plan
            nodes:
              - id: ship
                kind: publish
                package: X
                bump: minor
              - id: gate
                kind: test-run-gate
                depends_on: [ship]
            """).Succeeded.ShouldBeTrue();

        var detail = PlanViews.Detail(registry.Find("pushed-plan")!);

        detail.Source.ShouldBe("pushed");
        detail.Nodes![0].Kind.ShouldBe("publish");
        detail.Nodes[0].Bump.ShouldBe("minor");
        detail.Nodes[1].Kind.ShouldBe("test-run-gate");
        detail.DependencyOrder.ShouldBe(["ship", "gate"]);
    }
}
