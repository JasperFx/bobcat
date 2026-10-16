using System.Text.Json.Nodes;
using Bobcat.Rendering;
using Shouldly;

namespace Bobcat.Tests.Rendering;

/// <summary>
/// bobcat#604: the results schema is not committed, so the review signal is a CI check against the
/// last released package's copy. These pin both directions with planted changes to the real schema.
/// </summary>
public class ResultsSchemaCompatibilityTests
{
    private static readonly string current = ResultsFile.GenerateSchema();

    private static string planted(Action<JsonObject> change)
    {
        var schema = JsonNode.Parse(current)!.AsObject();
        change(schema);
        return schema.ToJsonString();
    }

    private static JsonObject run(JsonObject schema) => schema["properties"]!["run"]!.AsObject();

    [Fact]
    public void the_schema_is_compatible_with_itself()
    {
        ResultsSchemaCompatibility.BreakingChanges(current, current).ShouldBeEmpty();
    }

    [Fact]
    public void a_removed_property_is_breaking()
    {
        var next = planted(s => run(s)["properties"]!.AsObject().Remove("suite"));

        ResultsSchemaCompatibility.BreakingChanges(current, next).ShouldBe(["run.suite: property removed"]);
    }

    [Fact]
    public void a_changed_type_is_breaking_in_either_direction()
    {
        var narrowed = planted(s => run(s)["properties"]!["tag"]!["type"] = "string");
        ResultsSchemaCompatibility.BreakingChanges(current, narrowed)
            .ShouldBe(["run.tag: type changed from [null, string] to string"]);

        var replaced = planted(s => run(s)["properties"]!["suite"]!["type"] = "integer");
        ResultsSchemaCompatibility.BreakingChanges(current, replaced)
            .ShouldBe(["run.suite: type changed from string to integer"]);
    }

    [Fact]
    public void a_change_inside_an_array_item_is_found()
    {
        var next = planted(s =>
            s["properties"]!["scenarios"]!["items"]!["properties"]!.AsObject().Remove("feature"));

        ResultsSchemaCompatibility.BreakingChanges(current, next).ShouldBe(["scenarios[].feature: property removed"]);
    }

    [Fact]
    public void a_newly_required_property_is_breaking()
    {
        var next = planted(s => run(s)["required"] = new JsonArray("suite"));

        ResultsSchemaCompatibility.BreakingChanges(current, next).ShouldBe(["run.suite: newly required"]);
    }

    [Fact]
    public void a_new_optional_property_is_additive_and_passes()
    {
        var next = planted(s => run(s)["properties"]!.AsObject().Add("machine", new JsonObject
        {
            ["description"] = "A new, optional field",
            ["type"] = new JsonArray("string", "null")
        }));

        ResultsSchemaCompatibility.BreakingChanges(current, next).ShouldBeEmpty();
        ResultsSchemaCompatibility.Check(current, "1.0", next, "1.0", "the release").Passed.ShouldBeTrue();
    }

    [Fact]
    public void a_breaking_change_fails_the_check_unless_the_schema_version_moved()
    {
        var next = planted(s => run(s)["properties"]!.AsObject().Remove("suite"));

        var unchanged = ResultsSchemaCompatibility.Check(current, "1.0", next, "1.0", "Bobcat 0.61.0 on nuget.org");
        unchanged.Passed.ShouldBeFalse();
        unchanged.Lines.ShouldContain(l => l.Contains("run.suite: property removed"));
        unchanged.Lines.ShouldContain(l => l.Contains("SchemaVersion"));

        var moved = ResultsSchemaCompatibility.Check(current, "1.0", next, "2.0", "Bobcat 0.61.0 on nuget.org");
        moved.Passed.ShouldBeTrue();
        moved.Lines[0].ShouldContain("1.0 → 2.0");
    }

    [Fact]
    public void the_version_is_read_off_the_schema_file_name()
    {
        ResultsSchemaCompatibility.VersionOf("/x/schemas/" + ResultsFile.SchemaFileName).ShouldBe(ResultsFile.SchemaVersion);
        ResultsSchemaCompatibility.VersionOf("bobcat-results-2.1.schema.json").ShouldBe("2.1");
        ResultsSchemaCompatibility.VersionOf("something.json").ShouldBeNull();
    }
}
