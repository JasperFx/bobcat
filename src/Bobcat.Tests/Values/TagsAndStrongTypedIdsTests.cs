using Bobcat.Engine;
using Bobcat.Runtime;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.Tests.Values;

public readonly record struct ScreeningId(Guid Value);
public readonly record struct CustomerId(Guid Value);
public readonly record struct SeatCode(string Value);
public readonly record struct RowNumber(int Value);

// A strong-typed id written as a class, the way Vogen and StronglyTypedId types often are
public sealed class TicketId(Guid value)
{
    public Guid Value { get; } = value;
    public override bool Equals(object? obj) => obj is TicketId other && other.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}

public record SeatReserved(ScreeningId Screening, SeatCode Seat);
public record SeatReleased(SeatCode Seat);
public record OneThing(string Name);

// bobcat#433: a strong-typed id reads by its Guid's name, or as TypeName(value), however it is built
public class StrongTypedIdRenderingTests
{
    private static ScenarioRecorder.Recording scenario()
        => ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());

    [Fact]
    public void a_guid_based_id_reads_by_its_declared_name()
    {
        using var _ = scenario();
        var theScreeningId = ScenarioValues.Declare(new ScreeningId(Guid.NewGuid()), "theScreeningId");

        ScenarioValues.Format(theScreeningId).ShouldBe("theScreeningId");
        ScenarioValues.Describe(new SeatReserved(theScreeningId, new SeatCode("4C")))
            .ShouldBe("SeatReserved(Screening: theScreeningId, Seat: SeatCode(4C))");
    }

    [Fact]
    public void a_guid_based_id_nobody_declared_is_named_after_the_property_it_is_first_seen_under()
    {
        using var _ = scenario();
        var screening = new ScreeningId(Guid.NewGuid());

        ScenarioValues.Describe(new SeatReserved(screening, new SeatCode("4C")));

        ScenarioValues.Format(screening).ShouldBe("Screening");
    }

    [Fact]
    public void an_unnamed_guid_based_id_shows_its_type_and_a_short_guid()
    {
        var id = Guid.NewGuid();
        ScenarioValues.Format(new ScreeningId(id)).ShouldBe($"ScreeningId({id.ToString()[..8]}…)");
    }

    [Fact]
    public void string_and_number_ids_read_as_the_type_around_the_value_with_no_quotes()
    {
        ScenarioValues.Format(new SeatCode("SCR-104")).ShouldBe("SeatCode(SCR-104)");
        ScenarioValues.Format(new RowNumber(42)).ShouldBe("RowNumber(42)");
        ScenarioValues.DescribeProperties(new SeatReleased(new SeatCode("4C"))).ShouldBe("Seat: SeatCode(4C)");
    }

    [Fact]
    public void a_class_with_a_guid_value_is_an_id_too()
    {
        using var _ = scenario();
        var ticket = ScenarioValues.Declare(new TicketId(Guid.NewGuid()), "theTicketId");

        ScenarioValues.Format(ticket).ShouldBe("theTicketId");
    }

    [Fact]
    public void a_record_with_one_member_that_is_not_called_value_reads_as_it_always_has()
    {
        ScenarioValues.Describe(new OneThing("x")).ShouldBe("OneThing(Name: \"x\")");
        ScenarioValues.Describe(new KeyValuePair<string, string>("a", "b")).ShouldNotStartWith("KeyValuePair<String, String>(b");
    }
}

// JasperFx/wolverine#4865: an expected event may name the tags it must carry -- an exact, unordered match
public class TaggedExpectationTests
{
    private static readonly ScreeningId Screening = new(Guid.NewGuid());
    private static readonly CustomerId Customer = new(Guid.NewGuid());
    private static readonly SeatReserved Reserved = new(Screening, new SeatCode("4C"));

    private static TableRun verify(object actual, IReadOnlyList<object> tags, object expected, SetMode mode = SetMode.Ordered)
        => ObjectSetVerification.Verify([actual], [expected], "event", mode, _ => tags);

    private static string[] problems(TableRun run) => ObjectSetVerification.Problems(run, "event").ToArray();

    [Fact]
    public void the_same_tags_in_any_order_match()
    {
        verify(Reserved, [Customer, Screening], Specify<SeatReserved>().Tagged(Screening, Customer))
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void a_missing_tag_fails_on_the_tags()
    {
        var run = verify(Reserved, [Screening], Specify<SeatReserved>().Tagged(Screening, Customer));

        run.Succeeded.ShouldBeFalse();
        problems(run).ShouldHaveSingleItem().ShouldStartWith("FAIL SeatReserved: expected tags: [");
    }

    [Fact]
    public void an_extra_tag_fails_too_because_the_match_is_exact()
    {
        verify(Reserved, [Screening, Customer], Specify<SeatReserved>().Tagged(Screening))
            .Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void an_expectation_that_names_no_tags_never_judges_them()
    {
        verify(Reserved, [Screening, Customer], Specify<SeatReserved>().With(x => x.Seat, new SeatCode("4C")))
            .Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void a_whole_object_can_name_its_tags()
    {
        verify(Reserved, [Screening], Tagged(new SeatReserved(Screening, new SeatCode("4C")), Screening))
            .Succeeded.ShouldBeTrue();

        verify(Reserved, [Screening], Tagged(new SeatReserved(Screening, new SeatCode("9Z")), Screening))
            .Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void an_expected_value_keeps_the_members_it_ignores_when_tagged()
    {
        var expected = Tagged(Expect.Value(new SeatReserved(Screening, new SeatCode("9Z"))).Ignoring(x => x.Seat), Screening);
        verify(Reserved, [Screening], expected).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void every_tag_of_one_event_reads_on_its_one_row()
    {
        using var _ = ScenarioRecorder.Begin("Values", "a scenario", publisher: null, runId: Guid.NewGuid());
        ScenarioValues.Declare(Screening, "theScreeningId");
        ScenarioValues.Declare(Customer, "theCustomerId");

        PartialMatching.DescribeExpected(Specify<SeatReserved>().With(x => x.Seat, new SeatCode("4C")).Tagged(Screening, Customer))
            .ShouldBe("Seat: SeatCode(4C), tags: [theScreeningId, theCustomerId]");
    }

    [Fact]
    public void a_forbidden_event_with_tags_forbids_only_one_carrying_exactly_those()
    {
        var other = new ScreeningId(Guid.NewGuid());

        ObjectSetVerification.Absent([Reserved], [Specify<SeatReserved>().Tagged(other)], "event", _ => [Screening])
            .Succeeded.ShouldBeTrue();
        ObjectSetVerification.Absent([Reserved], [Specify<SeatReserved>().Tagged(Screening)], "event", _ => [Screening])
            .Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void tags_are_never_empty_and_a_partial_names_its_own()
    {
        Should.Throw<ArgumentException>(() => Specify<SeatReserved>().Tagged());
        Should.Throw<ArgumentException>(() => Tagged(Specify<SeatReserved>(), Screening));
    }
}
