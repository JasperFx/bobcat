using Bobcat.EventModel.Emlang;
using Bobcat.EventModel.Scenarios;
using Shouldly;

namespace Bobcat.EventModel.Tests;

/// <summary>
/// bobcat#846: every reader lowers a board example to one <see cref="ScenarioModel"/>, and the import binds
/// it to its slice. These pin the emlang lowering (what every reader means when it says no more than emlang
/// can), the binding, and the round trip back to the element-naming <see cref="EmlangTest"/>.
/// </summary>
public class ScenarioModelTests
{
    private const string Board =
        """
        slices:
          Registration:
            steps:
              - t: Customer/Registration Form
              - c: Register User
              - e: User/User Registered
              - v: User Profile
            tests:
              EmailMustBeUnique:
                given:
                  - e: User/User Registered
                    props: { email: joe@example.com }
                  - v: User Profile
                    props: { email: joe@example.com }
                when:
                  - c: Register User
                    props: { email: joe@example.com, password: Str0ng }
                then:
                  - x: Email Already In Use
                    props: { email: joe@example.com }
              Registers:
                when:
                  - c: Register User
                    props: { email: alice@example.com }
                then:
                  - e: User/User Registered
                    props: { email: alice@example.com }
                  - v: User Profile
                    props: { email: alice@example.com }
              ProfileExists:
                then:
                  - v: User Profile
        """;

    private static EmlangBoard board() => EmlangReader.Read(Board);

    [Fact]
    public void an_emlang_test_lowers_to_givens_an_act_and_typed_outcomes()
    {
        var model = board().Chapters.Single().Tests.Single(x => x.Name == "EmailMustBeUnique").Model;

        model.Name.ShouldBe("EmailMustBeUnique");
        model.GivenEvents.Single().ShouldSatisfyAllConditions(
            x => x.Event.ShouldBe("User Registered"),
            x => x.Values["email"].ShouldBe("joe@example.com"),
            x => x.Stream.ShouldBeNull("a swimlane is not a stream"),
            x => x.Tags.ShouldBeEmpty());
        model.Context.Single().ReadModel.ShouldBe("User Profile");

        var act = model.Act.ShouldBeOfType<MessageReceived>();
        act.Message.ShouldBe("Register User");
        act.Values["password"].ShouldBe("Str0ng");

        var refusal = model.Outcomes.Single().ShouldBeOfType<Refusal>();
        refusal.Reason.ShouldBe("Email Already In Use");
        refusal.Kind.ShouldBe(RefusalKind.Refused);
        refusal.Mentioned.ShouldBe(["joe@example.com"]);
        model.Refuses.ShouldBeTrue();
    }

    [Fact]
    public void a_then_event_is_emitted_and_a_then_view_with_values_is_a_partial_read_model()
    {
        var model = board().Chapters.Single().Tests.Single(x => x.Name == "Registers").Model;

        model.Outcomes.Count.ShouldBe(2);
        model.Outcomes[0].ShouldBeOfType<EventEmitted>().Values["email"].ShouldBe("alice@example.com");
        var view = model.Outcomes[1].ShouldBeOfType<ReadModelOutcome>();
        view.Shape.ShouldBe(ReadModelShape.Partial);
        view.Match.ShouldBe(MatchMode.Subset);
        view.Rows.ShouldBeEmpty();
    }

    [Fact]
    public void a_view_only_test_has_no_act_and_a_single_read_model_outcome()
    {
        var model = board().Chapters.Single().Tests.Single(x => x.Name == "ProfileExists").Model;

        model.Act.ShouldBeNull();
        model.Outcomes.Single().ShouldBeOfType<ReadModelOutcome>().Shape.ShouldBe(ReadModelShape.Single);
        model.Status.ShouldBe(ScenarioStatus.Normal);
        model.Slice.ShouldBeNull("a reader's output is unbound; the import binds it");
    }

    [Fact]
    public void the_import_binds_the_model_to_the_slice_it_attached_the_test_to()
    {
        var result = EmlangImport.ToCurated(board(), "Users");

        var slice = result.Model.Slices.Single(x => x.Name == "RegisterUser");
        var models = slice.Specifications!.Scenarios.Select(x => x.Model!).ToList();
        models.Count.ShouldBe(2);
        models.ShouldAllBe(x => x.Slice == "RegisterUser" && x.Chapter == "Registration" && x.Pattern == "Command" && x.Status == ScenarioStatus.Normal);

        // The assertion-only test lands on the view slice, bound to it
        var view = result.Model.Slices.Single(x => x.Name == "UserProfile");
        view.Specifications!.Scenarios.Single().Model!.ShouldSatisfyAllConditions(
            x => x.Slice.ShouldBe("UserProfile"),
            x => x.Pattern.ShouldBe("View"),
            x => x.Name.ShouldBe("ProfileExists"));
    }

    [Fact]
    public void the_model_round_trips_to_the_element_naming_shape()
    {
        var test = board().Chapters.Single().Tests.Single(x => x.Name == "EmailMustBeUnique");
        var again = test.Model.ToEmlangTest();

        again.Name.ShouldBe(test.Name);
        again.Given.Select(x => (x.Kind, x.Label)).ShouldBe(test.Given.Select(x => (x.Kind, x.Label)));
        again.When.Single().Props.ShouldBe(test.When.Single().Props);
        again.Then.Single().Kind.ShouldBe(EmlangElementKind.Error);
        again.Then.Single().Props.Values.ShouldBe(["joe@example.com"]);
        again.Model.ShouldBeSameAs(test.Model);
    }

    [Fact]
    public void a_model_a_reader_did_not_set_is_derived_from_the_test_once()
    {
        var test = new EmlangTest("T", [], [new EmlangRef(EmlangElementKind.Command, "", "Do")], []);

        test.Model.ShouldBeSameAs(test.Model);
        test.Model.Act.ShouldBeOfType<MessageReceived>().Message.ShouldBe("Do");
    }

    [Fact]
    public void the_outcome_vocabulary_covers_what_a_specification_can_state()
    {
        // The kinds the writer switches over (a closed set): one of each builds and reads back
        ScenarioOutcome[] outcomes =
        [
            new EventEmitted("E", ScenarioModel.None) { Stream = "Order", Tags = new Dictionary<string, string> { ["orderId"] = "o-1" }, IgnoredMembers = ["placedAt"] },
            new ReadModelOutcome("V", ReadModelShape.Rows, MatchMode.Exact, ScenarioModel.None) { Rows = [new Dictionary<string, string> { ["name"] = "Shoes" }] },
            new Refusal("too late", RefusalKind.ProblemDetails, ["2026-01-01"]),
            new MessageCaused("SendMail", ScenarioModel.None, Scheduled: true),
            new BoundaryExpectation("course c-1 subscriptions", ["StudentSubscribed"], ["CourseRenamed"]),
        ];
        ScenarioAct[] acts =
        [
            new MessageReceived("C", ScenarioModel.None),
            new MessagePublished("C", ScenarioModel.None),
            new HttpRequest("POST", "/orders/{id}", "PlaceOrder", ScenarioModel.None) { RouteValues = new Dictionary<string, string> { ["id"] = "o-1" } },
            new ReadModelQueried("Prices", new Dictionary<string, string> { ["at"] = "14:00" }),
            new TimeAdvances("2 days"),
            new ForeignEventReceived("PaymentSettled", ScenarioModel.None),
            new ScheduledMessagesExecute(),
        ];

        var model = new ScenarioModel("all") { Act = acts[2], Outcomes = outcomes, Rule = "one subscription per student", Clock = "2026-11-27T12:00Z", Tenant = "acme" };
        model.Refuses.ShouldBeTrue();
        model.Act.ShouldBeOfType<HttpRequest>().RouteValues["id"].ShouldBe("o-1");
        acts.Length.ShouldBe(7);
    }
}
