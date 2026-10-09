using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Partial;

/// <summary>bobcat#450: an expected object written as assertions on its members.</summary>
public class CheckingMembersTests
{
    private static readonly Shipped Actual = new(Guid.Parse("0199a3a0-0000-7000-8000-000000000002"), "UPS", "1Z999", 5m);

    [Fact]
    public void a_plain_should_be_is_an_ordinary_specified_value()
    {
        var specified = Specify<Shipped>(x => x.Carrier.ShouldBe("UPS"), x => x.Weight.ShouldBe(5m));

        specified.Values.Select(x => (x.Path, x.Value)).ShouldBe([("Carrier", (object?)"UPS"), ("Weight", 5m)]);
        PartialMatching.Differences(Actual, specified).ShouldBeEmpty();
        PartialMatching.Differences(Actual, Specify<Shipped>(x => x.Carrier.ShouldBe("FedEx"))).Count.ShouldBe(1);
    }

    [Fact]
    public void any_other_assertion_is_a_check_that_runs_against_the_member()
    {
        var holds = Specify<Shipped>(x => x.Weight.ShouldBeGreaterThan(3m), x => x.TrackingNumber.ShouldStartWith("1Z"));
        PartialMatching.Differences(Actual, holds).ShouldBeEmpty();

        var fails = Specify<Shipped>(x => x.Weight.ShouldBeGreaterThan(10m));
        var difference = PartialMatching.Differences(Actual, fails).Single();
        difference.Path.ShouldBe("Weight");
        difference.Expected!.ToString().ShouldBe("should be greater than 10");
    }

    [Fact]
    public void the_member_table_shows_each_check_as_it_reads_and_why_a_failed_one_failed()
    {
        var run = PropertyCells.Verify(Actual, Specify<Shipped>(
            x => x.Carrier.ShouldBe("UPS"),
            x => x.Weight.ShouldBeGreaterThan(10m)));

        run.Succeeded.ShouldBeFalse();
        var carrier = run.Cells.Single(x => x.Name == "Carrier");
        carrier.Status.ShouldBe(ResultStatus.success);
        carrier.Expected.ShouldBe("UPS");

        var weight = run.Cells.Single(x => x.Name == "Weight");
        weight.Status.ShouldBe(ResultStatus.failed);
        weight.Expected.ShouldBe("should be greater than 10");
        weight.Actual.ShouldBe("5");
        weight.Note.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void checks_chain_with_with_and_a_custom_message_is_not_part_of_the_expectation()
    {
        var specified = Specify<Shipped>()
            .With(x => x.Carrier, "UPS")
            .Check(x => x.Weight.ShouldBe(5m, "the parcel weight"));

        specified.Values.Single(x => x.Path == "Weight").Value.ShouldBe(5m);
    }

    [Fact]
    public void a_check_is_not_a_value_so_an_object_with_one_cannot_be_built()
    {
        Should.Throw<SpecCriticalException>(() => Specify<Shipped>(x => x.Weight.ShouldBeGreaterThan(3m)).Build())
            .Message.ShouldContain("is a check");
    }

    [Fact]
    public void a_check_must_name_a_member_and_compare_it_with_a_value()
    {
        Should.Throw<ArgumentException>(() => Specify<Shipped>(x => Console.WriteLine(x)));
        Should.Throw<ArgumentException>(() => Specify<Shipped>(x => x.Carrier.ShouldBe(x.TrackingNumber)));
    }
}
