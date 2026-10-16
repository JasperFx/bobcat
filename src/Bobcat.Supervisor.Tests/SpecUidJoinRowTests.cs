using Bobcat.Runtime;
using Shouldly;

namespace Bobcat.Supervisor.Tests;

/// <summary>
/// bobcat#569: a row of a data-driven spec shares its method's location with every other row, so the
/// identity → uid join must not name all of them for one row.
/// </summary>
public class SpecUidJoinRowTests
{
    private const string Parent = "Discounts/discount is applied";

    private static readonly SpecManifest manifest = new(
        SpecManifest.ProjectedLane, SpecManifest.XunitFramework, "Discounts.Tests",
        [
            new SpecManifestEntry(Parent, "Discounts.Tests.DiscountSpecs", "discount_is_applied"),
            new SpecManifestEntry(Parent + " (amount: 100)", "Discounts.Tests.DiscountSpecs", "discount_is_applied", Parent)
        ]);

    private static readonly IReadOnlyList<WorkerTest> discovered =
    [
        new("uid-1", "Discounts.Tests.DiscountSpecs.discount_is_applied(amount: 100)")
        {
            TestClass = "Discounts.Tests.DiscountSpecs", TestMethod = "discount_is_applied"
        },
        new("uid-2", "Discounts.Tests.DiscountSpecs.discount_is_applied(amount: 50)")
        {
            TestClass = "Discounts.Tests.DiscountSpecs", TestMethod = "discount_is_applied"
        }
    ];

    [Fact]
    public void the_method_identity_joins_every_row_and_a_row_identity_joins_none()
    {
        var map = SpecUidJoin.UidsByIdentity(manifest, discovered);

        map[Parent].ShouldBe(["uid-1", "uid-2"]);

        // Left out rather than joined to both rows: a warm command for one row is refused as an
        // identity whose uid cannot be named, instead of running the whole theory
        map.ShouldNotContainKey(Parent + " (amount: 100)");
    }
}
