using Bobcat.Console.Contracts;
using Bobcat.Console.Runs;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;

namespace Bobcat.Console.Tests;

/// <summary>
/// Issue #107, the read side: the touched-type run evidence a <c>scenario_finished</c> carries
/// is exposed per scenario by <c>GET /api/runs/{id}</c>, joined to the design-time descriptor by
/// the scenario's Uid and each type's FullName.
/// </summary>
public class RunDetailEvidenceTests : IDisposable
{
    private static readonly Guid run = Guid.Parse("9a1f1a1e-0000-0000-0000-000000000107");

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"bobcat-evidence-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dataPath, recursive: true); } catch { }
    }

    [Fact]
    public void the_run_detail_exposes_each_scenario_touched_types_and_finish_stamp()
    {
        var t0 = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        using var registry = new MonitorRunRegistry(_dataPath);
        registry.Record(
        [
            new RunStarted(run, "Wallets", "/repo", "main", "in-process", t0, 2),
            new ScenarioStarted(run, "Credit Wallet/happy path", "Credit Wallet", "happy path", 1, t0),
            new ScenarioFinished(run, "Credit Wallet/happy path", "CleanPass", 1, 120, null,
                TouchedTypes:
                [
                    new TouchedType("CreditWallet", "Wallets.CreditWallet", "Wallets"),
                    new TouchedType("WalletCredited", "Wallets.WalletCredited", "Wallets")
                ],
                At: t0.AddSeconds(2)),
            // A scenario from an evidence-blind publisher — the ledger stays empty, never null-ref.
            new ScenarioStarted(run, "Credit Wallet/sad path", "Credit Wallet", "sad path", 1, t0),
            new ScenarioFinished(run, "Credit Wallet/sad path", "Failed", 1, 80, "rejected"),
            new RunFinished(run, 1, 1, 1, 0, 0, t0.AddSeconds(3))
        ]);

        var detail = RunEndpoints.Find(run, registry).ShouldBeOfType<Ok<RunDetail>>().Value.ShouldNotBeNull();

        var evidenced = detail.Scenarios.Single(s => s.Uid == "Credit Wallet/happy path");
        evidenced.TouchedTypes.Select(t => t.FullName)
            .ShouldBe(["Wallets.CreditWallet", "Wallets.WalletCredited"]);
        evidenced.TouchedTypes[0].Name.ShouldBe("CreditWallet");
        evidenced.TouchedTypes[0].AssemblyName.ShouldBe("Wallets");
        evidenced.FinishedAt.ShouldBe(t0.AddSeconds(2));

        var blind = detail.Scenarios.Single(s => s.Uid == "Credit Wallet/sad path");
        blind.TouchedTypes.ShouldBeEmpty();
        blind.FinishedAt.ShouldBeNull();
    }
}
