using Bobcat.Ui.Contracts;
using Shouldly;

namespace Bobcat.Ui.Tests;

/// <summary>
/// The TypeScript mirrors of the monitor contracts are generated, not hand-written (issue #85),
/// and not committed either (bobcat#604): the Nuke <c>UiContracts</c> target and the frontend's
/// npm pre-hooks write them, and <c>ui-frontend.yml</c> type-checks, tests and builds the SPA
/// against what was just generated. These pin what the generator emits.
/// </summary>
public class TypeScriptContractTests
{
    [Fact]
    public void every_derived_event_is_mirrored_with_its_wire_name()
    {
        var generated = TypeScriptContracts.GenerateMonitorEvents();

        TypeScriptContracts.EventTypes.ShouldNotBeEmpty();
        foreach (var (type, wireName) in TypeScriptContracts.EventTypes)
        {
            generated.ShouldContain($"export interface {type.Name} extends MonitorEvent {{");
            generated.ShouldContain($"/** Envelope type: '{wireName}' */");
            generated.ShouldContain($"  | '{wireName}'");
        }

        // The transport shapes ride along so relayToStore has one import.
        generated.ShouldContain("export interface MonitorEnvelope {");
        generated.ShouldContain("export interface BatchedWebSocketPayload {");
    }

    [Fact]
    public void generation_is_deterministic()
    {
        TypeScriptContracts.GenerateMonitorEvents().ShouldBe(TypeScriptContracts.GenerateMonitorEvents());
        TypeScriptContracts.GenerateRelayRoutes().ShouldBe(TypeScriptContracts.GenerateRelayRoutes());
    }

    [Fact]
    public void a_record_member_with_a_default_is_an_optional_member_because_an_old_publisher_may_omit_it()
    {
        // RunStarted.Tag was added after the first publishers shipped; their JSON has no member
        // at all, and the mirror says so rather than promising a null.
        TypeScriptContracts.GenerateMonitorEvents().ShouldContain("  tag?: string | null");

        // Same rule for the run evidence (issue #107) — and the nested TouchedType record is
        // mirrored as its own interface, reached through ScenarioFinished.
        TypeScriptContracts.GenerateMonitorEvents().ShouldContain("  touchedTypes?: TouchedType[] | null");
        TypeScriptContracts.GenerateMonitorEvents().ShouldContain("export interface TouchedType {");
    }

    [Fact]
    public void every_event_is_routed_to_its_runs_store_handler()
    {
        var routes = TypeScriptContracts.GenerateRelayRoutes();

        foreach (var (type, wireName) in TypeScriptContracts.EventTypes)
        {
            routes.ShouldContain($"    case '{wireName}':\n      runs.handle{type.Name}(envelope.data as {type.Name})\n      return true");
        }

        routes.ShouldContain("export function routeMonitorEvent(runs: RunsStore, envelope: MonitorEnvelope): boolean {");
        routes.ShouldContain("    default:\n      return false");

        // The import block lists exactly what the table casts to, sorted, plus the envelope.
        var expectedImports = TypeScriptContracts.EventTypes.Select(e => e.Type.Name)
            .Append("MonitorEnvelope")
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => $"  {n},");
        routes.ShouldContain("import type {\n" + string.Join('\n', expectedImports) + "\n} from './monitor-events'");
    }

    [Fact]
    public void the_routes_leave_the_envelopes_that_are_not_monitor_events_to_the_hand_written_relay()
    {
        var routes = TypeScriptContracts.GenerateRelayRoutes();

        routes.ShouldNotContain("batched_web_socket_payload");
        routes.ShouldNotContain("event_model_changed");
    }

    [Fact]
    public void the_hand_written_relay_delegates_to_the_generated_routes()
    {
        var frontEnd = TypeScriptContracts.FindFrontEndSourceDirectory();
        frontEnd.ShouldNotBeNull("the test must run from inside the repository to find src/Bobcat.Ui.FrontEnd/src");
        var relay = File.ReadAllText(Path.Combine(frontEnd, TypeScriptContracts.MessagesDirectory, "relayToStore.ts"));

        relay.ShouldContain("import { routeMonitorEvent } from './relayRoutes'");
        relay.ShouldContain("routeMonitorEvent(useRunsStore(), envelope)");
    }

    [Fact]
    public void the_command_writes_every_file_and_a_second_run_changes_nothing()
    {
        var frontEnd = Path.Combine(Path.GetTempPath(), "bobcat-ui-contracts", Guid.NewGuid().ToString("N"));
        var schema = Bobcat.Rendering.ResultsFile.SchemaText;
        try
        {
            var first = GenerateCommand.Write(frontEnd, schema);
            first.Select(r => Path.GetFileName(r.Path)).ShouldBe(TypeScriptContracts.GeneratedFiles);
            first.ShouldAllBe(r => r.Changed);

            File.ReadAllText(first[0].Path).ShouldBe(TypeScriptContracts.GenerateMonitorEvents());
            File.ReadAllText(first[1].Path).ShouldBe(TypeScriptContracts.GenerateRelayRoutes());
            File.ReadAllText(first[2].Path).ShouldBe(TypeScriptContracts.GenerateResultsFile(schema));

            GenerateCommand.Write(frontEnd, schema).ShouldAllBe(r => !r.Changed);
        }
        finally
        {
            if (Directory.Exists(frontEnd)) Directory.Delete(frontEnd, recursive: true);
        }
    }

    /// <summary>
    /// The results page (bobcat#541) reads these by name. The schema's object types are anonymous,
    /// so a name falling back to NJsonSchema's (<c>Scenarios2</c>, <c>ScenarioFailureCells</c>) is a
    /// renamed type the page's imports stop finding — or, worse, one that still compiles.
    /// </summary>
    [Fact]
    public void the_results_file_types_are_named_for_what_they_carry()
    {
        var generated = TypeScriptContracts.GenerateResultsFile(Bobcat.Rendering.ResultsFile.SchemaText);

        var names = System.Text.RegularExpressions.Regex.Matches(generated, @"^export interface (\w+) \{", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        names.ShouldBe([
            "BobcatResults", "ResultsRun", "ResultsSummary", "ResultsScenario", "ResultsTiming", "ResultsReport",
            "ResultsCounts", "ResultsStep", "ResultsCell", "ResultsAttachment", "ResultsScenarioTiming",
            "ResultsStepCost", "ResultsGap", "ResultsGrid", "ResultsGridRow"
        ], ignoreOrder: true);

        generated.ShouldContain("  setVerification?: ResultsGrid | null");
        generated.ShouldContain("  scenarioFailureCells?: ResultsCell[] | null");
        generated.ShouldNotContain("[key: string]: any");
    }

    [Fact]
    public void the_generated_files_are_gitignored()
    {
        var frontEnd = TypeScriptContracts.FindFrontEndSourceDirectory();
        frontEnd.ShouldNotBeNull();
        var ignore = File.ReadAllText(Path.Combine(frontEnd, "..", ".gitignore"));

        foreach (var file in TypeScriptContracts.GeneratedFiles)
        {
            ignore.ShouldContain($"src/messages/{file}");
        }
    }
}
