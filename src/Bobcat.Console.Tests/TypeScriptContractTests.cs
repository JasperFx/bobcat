using Bobcat.Console.Contracts;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// The TypeScript mirrors of the monitor contracts are generated, not hand-written (issue #85),
/// and these tests are the CI gate that keeps the committed files equal to what the C# records
/// generate. A contract change that forgets to regenerate is a red build here — never a
/// dashboard quietly reading a field that is not there.
/// </summary>
public class TypeScriptContractTests
{
    private static string frontEndMessages()
    {
        var frontEnd = TypeScriptContracts.FindFrontEndSourceDirectory();
        frontEnd.ShouldNotBeNull("the test must run from inside the repository to find src/Bobcat.Console.FrontEnd/src");
        return Path.Combine(frontEnd, TypeScriptContracts.MessagesDirectory);
    }

    private static string read(string file)
        => File.ReadAllText(Path.Combine(frontEndMessages(), file)).Replace("\r\n", "\n");

    [Fact]
    public void the_committed_monitor_events_ts_is_exactly_what_the_generator_emits()
    {
        var committed = read(TypeScriptContracts.MonitorEventsFile);
        var generated = TypeScriptContracts.GenerateMonitorEvents();

        committed.ShouldBe(generated,
            $"{TypeScriptContracts.MonitorEventsFile} has drifted from MonitorEvents.cs — run `{TypeScriptContracts.GenerateCommandLine}` and commit the result");
    }

    [Fact]
    public void the_committed_relay_to_store_routes_every_event_and_imports_what_it_routes()
    {
        var committed = read(TypeScriptContracts.RelayToStoreFile);

        // The patch is a no-op on a current file: every event has its case, the import block
        // lists every routed type. A new record without a case would come back inserted here.
        TypeScriptContracts.PatchRelayToStore(committed).ShouldBe(committed,
            $"{TypeScriptContracts.RelayToStoreFile} is missing a case or import — run `{TypeScriptContracts.GenerateCommandLine}` and commit the result");
    }

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
    public void patching_inserts_a_missing_case_above_the_marker_and_leaves_existing_cases_alone()
    {
        var relay = string.Join('\n',
        [
            "import { useRunsStore } from '@/stores/runs-store'",
            "import type {",
            "  MonitorEnvelope,",
            "  RunStarted,",
            "} from './monitor-events'",
            "",
            "export function relayToStore(message: unknown): void {",
            "  const runs = useRunsStore()",
            "  switch (envelope.type) {",
            "    case 'run_started':",
            "      runs.handleRunStarted(envelope.data as RunStarted) // hand-written, stays verbatim",
            "      break",
            $"    {TypeScriptContracts.CaseMarker}",
            "    default:",
            "      break",
            "  }",
            "}",
            ""
        ]);

        var patched = TypeScriptContracts.PatchRelayToStore(relay);

        // The hand-written case survived untouched, including its comment.
        patched.ShouldContain("runs.handleRunStarted(envelope.data as RunStarted) // hand-written, stays verbatim");
        patched.Split("case 'run_started':").Length.ShouldBe(2);

        // Every other event gained a case, each above the marker, in the store's handler convention.
        foreach (var (type, wireName) in TypeScriptContracts.EventTypes.Where(e => e.WireName != "run_started"))
        {
            patched.ShouldContain($"    case '{wireName}':\n      runs.handle{type.Name}(envelope.data as {type.Name})\n      break");
            patched.IndexOf($"case '{wireName}':", StringComparison.Ordinal)
                .ShouldBeLessThan(patched.IndexOf(TypeScriptContracts.CaseMarker, StringComparison.Ordinal));
        }

        // And the import block now lists everything it routes, sorted, plus the envelope shapes.
        var expectedImports = TypeScriptContracts.EventTypes.Select(e => e.Type.Name)
            .Append("MonitorEnvelope")
            .Append("BatchedWebSocketPayload")
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => $"  {n},");
        patched.ShouldContain("import type {\n" + string.Join('\n', expectedImports) + "\n} from './monitor-events'");

        // Idempotent: a second pass changes nothing.
        TypeScriptContracts.PatchRelayToStore(patched).ShouldBe(patched);
    }

    [Fact]
    public void patching_a_relay_with_no_marker_and_a_missing_case_fails_loudly_instead_of_guessing()
    {
        var relay = "import type {\n  MonitorEnvelope,\n} from './monitor-events'\nswitch (x) {\n  default:\n    break\n}\n";

        var e = Should.Throw<InvalidOperationException>(() => TypeScriptContracts.PatchRelayToStore(relay));
        e.Message.ShouldContain(TypeScriptContracts.CaseMarker);
    }
}
