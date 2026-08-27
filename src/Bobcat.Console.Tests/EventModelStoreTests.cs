using System.Text.Json;
using Bobcat.Console.EventModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #108, the descriptor wire: PUT /api/event-model validates and stores the current
/// descriptor (latest wins, normalized to the shape the shared renderer types), GET reads it
/// back, and the document survives a restart beside the run archives.
/// </summary>
public class EventModelStoreTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-event-model-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    private const string wallets =
        """
        {
          "name": "Wallets",
          "slices": [
            {
              "name": "CreditWallet",
              "domain": "Wallets",
              "pattern": "Command",
              "commandType": { "name": "CreditWallet", "fullName": "Wallets.CreditWallet", "assemblyName": "Wallets" },
              "emittedEvents": [{ "name": "WalletCredited", "fullName": "Wallets.WalletCredited", "assemblyName": "Wallets" }],
              "projectionTypes": [],
              "readModelTypes": [],
              "specifications": [{ "identity": "Wallet/Crediting a wallet", "resolvedTypes": [] }]
            }
          ]
        }
        """;

    [Fact]
    public void an_empty_store_reads_null_and_the_endpoint_404s()
    {
        var store = new EventModelStore(_dataPath);
        store.Read().ShouldBeNull();
        EventModelEndpoints.Get(store).ShouldBeOfType<NotFound>();
    }

    [Fact]
    public void a_descriptor_round_trips_normalized_with_the_computed_elements_on_it()
    {
        var store = new EventModelStore(_dataPath);
        store.TryStore(wallets).ShouldBeNull();

        var stored = JsonDocument.Parse(store.Read()!).RootElement;
        stored.GetProperty("name").GetString().ShouldBe("Wallets");

        var slice = stored.GetProperty("slices")[0];
        // Enum values stay PascalCase — the casing the shared renderer's TS mirror types.
        slice.GetProperty("pattern").GetString().ShouldBe("Command");
        // The typed round trip regenerates Elements/Edges — the rendering contract the SPA
        // draws from — so a producer that omitted them still yields a drawable document.
        slice.GetProperty("elements").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString())
            .ShouldBe(["Command", "Event"]);
        slice.GetProperty("edges").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void camelCase_enum_values_from_another_producer_are_normalized_not_rejected()
    {
        var store = new EventModelStore(_dataPath);
        store.TryStore(wallets.Replace("\"Command\"", "\"command\"")).ShouldBeNull();

        JsonDocument.Parse(store.Read()!).RootElement
            .GetProperty("slices")[0].GetProperty("pattern").GetString().ShouldBe("Command");
    }

    [Fact]
    public void garbage_is_rejected_with_the_reason_and_the_stored_document_stands()
    {
        var store = new EventModelStore(_dataPath);
        store.TryStore(wallets).ShouldBeNull();

        store.TryStore("not json at all").ShouldNotBeNull();
        store.TryStore("{}").ShouldBe("the descriptor has no name");

        JsonDocument.Parse(store.Read()!).RootElement.GetProperty("name").GetString().ShouldBe("Wallets");
    }

    [Fact]
    public void the_document_survives_a_restart()
    {
        new EventModelStore(_dataPath).TryStore(wallets).ShouldBeNull();

        var restarted = new EventModelStore(_dataPath);
        JsonDocument.Parse(restarted.Read()!).RootElement.GetProperty("name").GetString().ShouldBe("Wallets");
    }

    [Fact]
    public void latest_push_wins_wholesale()
    {
        var store = new EventModelStore(_dataPath);
        store.TryStore(wallets).ShouldBeNull();
        store.TryStore("""{ "name": "Orders", "slices": [] }""").ShouldBeNull();

        var stored = JsonDocument.Parse(store.Read()!).RootElement;
        stored.GetProperty("name").GetString().ShouldBe("Orders");
        stored.GetProperty("slices").GetArrayLength().ShouldBe(0);
    }
    // Issue #169 — the change broadcast names the model it is about, so a console watching more
    // than one producer can say WHICH one moved rather than just "something changed".
    [Fact]
    public void the_stored_name_is_available_for_the_change_broadcast()
    {
        var store = new EventModelStore(_dataPath);
        store.Name.ShouldBeNull("nothing has been published yet");

        store.TryStore(wallets).ShouldBeNull();
        store.Name.ShouldBe("Wallets");

        store.TryStore("""{ "name": "Orders", "slices": [] }""").ShouldBeNull();
        store.Name.ShouldBe("Orders", "latest wins, for the name as much as for the document");
    }

    [Fact]
    public void a_rejected_push_leaves_the_name_alone()
    {
        // The broadcast only fires on success, but the name must not move either — a 400 has not
        // changed what the page would load.
        var store = new EventModelStore(_dataPath);
        store.TryStore(wallets).ShouldBeNull();

        store.TryStore("not json at all").ShouldNotBeNull();

        store.Name.ShouldBe("Wallets");
    }

    [Fact]
    public void the_name_survives_a_restart()
    {
        // Recovered from disk on construction, so the first broadcast after a console restart is
        // not anonymous.
        new EventModelStore(_dataPath).TryStore(wallets).ShouldBeNull();

        new EventModelStore(_dataPath).Name.ShouldBe("Wallets");
    }

    [Fact]
    public void a_corrupt_document_on_disk_does_not_stop_the_console_booting()
    {
        // Best-effort by design: Read() has always handed the bytes back untouched whatever they
        // are, and recovering the NAME must not be the thing that turns a bad file into a crash.
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, "event-model.json"), "{ this is not json");

        var store = new EventModelStore(_dataPath);

        store.Name.ShouldBeNull();
        store.Read().ShouldBe("{ this is not json");
    }

}
