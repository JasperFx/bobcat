using Shouldly;

namespace Bobcat.Generators.Tests;

/// <summary>
/// BOBCAT044 (bobcat#624): a member check the runtime cannot read as one assertion on one member path —
/// chained Shouldly, mostly — is a build error at the lambda, instead of an <c>ArgumentException</c> that
/// ends the test at run time.
/// </summary>
public class MemberCheckDiagnosticTests
{
    private const string Types =
        """
        using System;
        using System.Collections.Generic;
        using System.Linq.Expressions;
        using Bobcat;
        using Shouldly;
        using static Bobcat.Specifications;

        namespace Specs;

        public record Attendee(Guid MemberId, string Name);
        public record Meeting(string Title, int Capacity, List<Attendee> Attendees, Address Where);
        public record Address(string City);

        // A grammar's own forwarding wrapper, the shape CritterStackSpec.Specify has
        public abstract class Grammar
        {
            protected static Specified<T> Check<T>(params Expression<Action<T>>[] checks) => Specifications.Specify(checks);
        }
        """;

    private static GeneratorHarness.RunOutcome run(string body)
        => GeneratorHarness.Run(Types + "\n" + $$"""
            public class Specs : Grammar
            {
                public void Run(Guid id)
                {
            {{body}}
                }
            }
            """);

    private static IReadOnlyList<string> messages(GeneratorHarness.RunOutcome outcome)
        => outcome.WithId("BOBCAT044").Select(d => d.GetMessage()).ToList();

    [Fact]
    public void a_check_on_one_member_path_is_clean()
    {
        var outcome = run("""
                          Specify<Meeting>(x => x.Title.ShouldBe("Standup"), x => x.Capacity.ShouldBeGreaterThan(3),
                              x => x.Where.City.ShouldStartWith("Aus"), x => x.Attendees.ShouldNotBeEmpty());
                          Specify<Meeting>().With(x => x.Title, "Standup").Check(x => x.Capacity.ShouldBeLessThan(10, "room size"));
                          Check<Meeting>(x => x.Title.ShouldBe(id.ToString()));
                          """);

        messages(outcome).ShouldBeEmpty();

        // CS0854 is the harness's language version (before C# 14 an expression tree cannot leave out
        // Shouldly's optional customMessage), not something wrong with the checks
        outcome.CompilationErrors.Where(x => x.Id != "CS0854").ShouldBeEmpty();
    }

    [Fact]
    public void a_chained_assertion_on_a_collection_member_is_an_error_at_the_lambda()
    {
        var outcome = run("""
                          Specify<Meeting>(x => x.Attendees.ShouldHaveSingleItem().MemberId.ShouldBe(id));
                          """);

        var error = outcome.WithId("BOBCAT044").ShouldHaveSingleItem();
        error.Severity.ShouldBe(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        error.GetMessage().ShouldContain("x.Attendees.ShouldHaveSingleItem().MemberId");
        error.GetMessage().ShouldContain("Expect.Exactly(");

        var span = error.Location.SourceSpan;
        error.Location.SourceTree!.ToString().Substring(span.Start, span.Length)
            .ShouldBe("x => x.Attendees.ShouldHaveSingleItem().MemberId.ShouldBe(id)");
    }

    [Fact]
    public void a_chained_assertion_on_the_object_itself_is_an_error()
    {
        // The issue's second shape: Specify<List<MeetingGroup>>(x => x.ShouldHaveSingleItem().Name.ShouldBe("…"))
        var outcome = run("""
                          Specify<List<Attendee>>(x => x.ShouldHaveSingleItem().Name.ShouldBe("Ann"));
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("x.ShouldHaveSingleItem().Name");
    }

    [Fact]
    public void an_assertion_on_the_parameter_with_no_member_is_an_error()
    {
        var outcome = run("""
                          Specify<Attendee>(x => x.ShouldNotBeNull());
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("not a member of the object");
    }

    [Fact]
    public void comparing_two_members_of_the_object_is_an_error()
    {
        var outcome = run("""
                          Specify<Meeting>(x => x.Title.ShouldBe(x.Where.City));
                          """);

        messages(outcome).ShouldHaveSingleItem().ShouldContain("compares the member with another member");
    }

    [Fact]
    public void every_bad_lambda_is_reported_and_only_those()
    {
        var outcome = run("""
                          Specify<Meeting>(
                              x => x.Title.ShouldBe("Standup"),
                              x => x.Attendees.ShouldHaveSingleItem().Name.ShouldBe("Ann"),
                              x => x.Attendees[0].Name.ShouldBe("Ann"));
                          """);

        messages(outcome).Count.ShouldBe(2);
    }

    [Fact]
    public void the_check_method_and_a_grammars_own_wrapper_are_checked_too()
    {
        var outcome = run("""
                          Specify<Meeting>().With(x => x.Title, "Standup").Check(x => x.Attendees.ShouldHaveSingleItem().Name.ShouldBe("Ann"));
                          Check<Meeting>(x => x.Attendees.ShouldHaveSingleItem().Name.ShouldBe("Ann"));
                          """);

        messages(outcome).Count.ShouldBe(2);
    }

    [Fact]
    public void a_lambda_that_is_not_a_member_check_is_left_alone()
    {
        // Not a Bobcat sink and not a Specified<T> wrapper: nothing to say about it
        var outcome = run("""
                          Func<Meeting, string> pick = x => x.Attendees.ShouldHaveSingleItem().Name;
                          new List<Attendee>().ForEach(x => x.ShouldNotBeNull());
                          """);

        messages(outcome).ShouldBeEmpty();
    }
}
