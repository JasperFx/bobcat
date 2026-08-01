using Bobcat.Monitor.Coordination;
using Shouldly;

namespace Bobcat.Monitor.Tests;

public class PlanSchemaTests
{
    private static PlanParseResult parse(string yaml) => PlanParser.Parse(yaml);

    private static PlanDocument parseValid(string yaml)
    {
        var result = PlanParser.Parse(yaml);
        result.Errors.ShouldBeEmpty();
        return result.Document!;
    }

    // A tiny well-formed document the failure tests mutate from.
    private const string Minimal =
        """
        schema: 1
        plan: tiny
        repos:
          bobcat: JasperFx/bobcat
        nodes:
          - id: fix-it
            kind: issue
            repo: bobcat
        """;

    [Fact]
    public void parses_the_epics_own_plan()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", "agent-coordination-epic.yaml"));
        var document = parseValid(yaml);

        document.Plan.ShouldBe("agent-coordination-epic");
        document.Title.ShouldBe("AI Agent Coordination in Bobcat Monitor");
        document.Nodes.Count.ShouldBe(10);

        var publish = document.FindNode("publish-sqlite-store")!;
        publish.Kind.ShouldBe(PlanNodeKind.Publish);
        publish.Package.ShouldBe("JasperFx.Events.Sqlite");
        publish.Bump.ShouldBe(BumpKind.Minor);
        publish.Feed.ShouldBe("nuget.org"); // the default, no feed declared

        var consume = document.FindNode("consume-sqlite-store")!;
        consume.Repo.ShouldBe("JasperFx/bobcat"); // alias resolved at parse time

        var gate = document.FindNode("monitor-suite-green")!;
        gate.Kind.ShouldBe(PlanNodeKind.TestRunGate);
        gate.DependsOn.ShouldBe(["mcp-tools", "dag-view", "nuget-observer"]);
    }

    [Fact]
    public void issues_may_be_planned_before_they_exist()
    {
        var document = parseValid(Minimal);
        var node = document.Nodes.Single();

        node.Issue.ShouldBeNull(); // unbound until an agent materializes it
        node.Merge.ShouldBe(MergePolicy.ManualReview); // never merge-on-green by omission
        node.Title.ShouldBe("fix-it"); // falls back to the id
    }

    [Fact]
    public void dependency_order_always_puts_upstream_first()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Plans", "agent-coordination-epic.yaml"));
        var ordered = parseValid(yaml).InDependencyOrder;

        var position = ordered.Select((node, index) => (node.Id, index)).ToDictionary(x => x.Id, x => x.index);
        foreach (var node in ordered)
        {
            foreach (var dependency in node.DependsOn)
            {
                position[dependency].ShouldBeLessThan(position[node.Id],
                    $"'{dependency}' should sort before '{node.Id}'");
            }
        }
    }

    [Fact]
    public void a_dependency_cycle_is_reported_with_its_path()
    {
        var result = parse(
            """
            schema: 1
            plan: cyclic
            nodes:
              - id: a
                kind: test-run-gate
                depends_on: [c]
              - id: b
                kind: test-run-gate
                depends_on: [a]
              - id: c
                kind: test-run-gate
                depends_on: [b]
            """);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.Contains("dependency cycle") && x.Contains("->"));
    }

    [Fact]
    public void gathers_every_error_instead_of_stopping_at_the_first()
    {
        var result = parse(
            """
            schema: 2
            plan: Bad Slug
            nodes:
              - id: pub
                kind: publish
              - id: pub
                kind: consume
                repo: nowhere
                package: X
              - id: who
                kind: issue
                repo: JasperFx/bobcat
                depends_on: [ghost, who]
            """);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.Contains("unknown schema version 2"));
        result.Errors.ShouldContain(x => x.Contains("plan slug 'Bad Slug'"));
        result.Errors.ShouldContain(x => x.Contains("publish nodes need a 'package'"));
        result.Errors.ShouldContain(x => x.Contains("publish nodes need a 'bump'"));
        result.Errors.ShouldContain(x => x.Contains("id 'pub' is declared more than once"));
        result.Errors.ShouldContain(x => x.Contains("repo alias 'nowhere' is not declared"));
        result.Errors.ShouldContain(x => x.Contains("depends on unknown node 'ghost'"));
        result.Errors.ShouldContain(x => x.Contains("depends on itself"));
    }

    [Fact]
    public void fields_that_do_not_apply_to_a_kind_are_refused()
    {
        var result = parse(
            """
            schema: 1
            plan: confused
            nodes:
              - id: gate
                kind: test-run-gate
                package: Should.Not.Be.Here
              - id: ship
                kind: publish
                package: X
                bump: fix
                merge: merge-on-green
            """);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(x => x.Contains("node 'gate'") && x.Contains("'package' does not apply"));
        result.Errors.ShouldContain(x => x.Contains("node 'ship'") && x.Contains("'merge' does not apply"));
    }

    [Fact]
    public void an_unknown_key_is_an_error_not_a_shrug()
    {
        // depends_on misspelled — silently ignoring it would erase a dependency edge.
        var result = parse(
            """
            schema: 1
            plan: typo
            nodes:
              - id: a
                kind: test-run-gate
              - id: b
                kind: test-run-gate
                depend_on: [a]
            """);

        result.Succeeded.ShouldBeFalse();
        result.Errors.Single().ShouldContain("unknown key 'depend_on'");
        result.Errors.Single().ShouldNotContain("PlanDto"); // internals never reach the wire
    }

    [Fact]
    public void unknown_enum_values_name_the_alternatives()
    {
        var result = parse(
            """
            schema: 1
            plan: enums
            nodes:
              - id: a
                kind: shipment
              - id: b
                kind: publish
                package: X
                bump: patch
            """);

        result.Errors.ShouldContain(x => x.Contains("unknown kind 'shipment'") && x.Contains("test-run-gate"));
        result.Errors.ShouldContain(x => x.Contains("unknown bump 'patch'") && x.Contains("fix, minor, major"));
    }

    [Fact]
    public void a_literal_org_slash_name_repo_needs_no_alias()
    {
        var document = parseValid(
            """
            schema: 1
            plan: literal
            nodes:
              - id: a
                kind: issue
                repo: OtherOrg/other-repo
            """);

        document.Nodes.Single().Repo.ShouldBe("OtherOrg/other-repo");
    }

    [Fact]
    public void anchor_must_be_an_issue_reference()
    {
        parse(Minimal.Replace("plan: tiny", "plan: tiny\nanchor: not-an-issue-ref"))
            .Errors.ShouldContain(x => x.Contains("org/repo#123"));

        parseValid(Minimal.Replace("plan: tiny", "plan: tiny\nanchor: JasperFx/bobcat#90"))
            .Anchor.ShouldBe("JasperFx/bobcat#90");
    }
}
