using Bobcat.Engine;
using Shouldly;

namespace Bobcat.CritterStack.Tests;

/// <summary>
/// Issue #241: a <c>Given</c> arranges history, and history is arranged by the fields the decision
/// reads — never by every field the event happens to carry.
/// </summary>
/// <remarks>
/// The failure this fixes took out all thirteen scenarios of a scaffolded chapter at once:
/// <c>Cannot build 'HomeCheckAppointmentProposed' from the columns [ownerId, proposedFor]</c>, for
/// a six-parameter record. It also put the curated model in an impossible position — <c>elements:</c>
/// hints exist so records are rich, scenario <c>with:</c> exists so specs are pointed, and the first
/// made the second impossible: enriching an event to six honest fields broke every scenario that
/// arranged it with two.
/// </remarks>
public class PartialArrangementTests
{
    private record Proposed(Guid AppointmentId, Guid OwnerId, Guid ShelterId, Guid DogId, DateTimeOffset ProposedFor);

    private static readonly Guid Owner = Guid.Parse("0e5e0001-0000-0000-0000-000000000001");

    [Fact]
    public void a_given_arranges_the_fields_it_names_and_defaults_the_rest()
    {
        var built = (Proposed)RecordBuilding.Build(typeof(Proposed), new Dictionary<string, string>
        {
            ["OwnerId"] = Owner.ToString(), ["ProposedFor"] = "2026-10-01T15:00:00Z",
        }, partial: true);

        built.OwnerId.ShouldBe(Owner);
        built.ProposedFor.ShouldBe(DateTimeOffset.Parse("2026-10-01T15:00:00Z"));

        // The scenario said nothing about these, so they say nothing back.
        built.AppointmentId.ShouldBe(Guid.Empty);
        built.ShelterId.ShouldBe(Guid.Empty);
        built.DogId.ShouldBe(Guid.Empty);
    }

    [Fact]
    public void an_act_is_still_complete_because_a_commands_fields_are_the_scenario()
    {
        // Deliberately NOT relaxed. A Given describes history the scenario did not author; an act
        // is the scenario's own input, so a missing field is a spec that tests something other
        // than what it says — and #233's named refusal is the right answer there.
        Should.Throw<SpecCriticalException>(() =>
            RecordBuilding.Build(typeof(Proposed), new Dictionary<string, string>
            {
                ["OwnerId"] = Owner.ToString(),
            }));
    }

    [Fact]
    public void a_column_matching_nothing_is_refused_by_name()
    {
        // The case actually worth failing on. Relaxing the missing-column rule removed the
        // accident that used to catch a typo, so it is checked deliberately instead.
        var ex = Should.Throw<SpecCriticalException>(() =>
            RecordBuilding.Build(typeof(Proposed), new Dictionary<string, string>
            {
                ["OwnerId"] = Owner.ToString(), ["ProposdFor"] = "2026-10-01T15:00:00Z",
            }, partial: true));

        ex.Message.ShouldContain("ProposdFor");
        ex.Message.ShouldContain("match nothing on 'Proposed'");
    }

    [Fact]
    public void an_empty_unmatched_cell_is_a_union_table_not_a_typo()
    {
        // One `Given events for …` table may carry rows of several event types; its header is then
        // the union of their fields, blank where a column does not apply to that row. A typo always
        // arrives with a value in it, so emptiness is the discriminator.
        var built = (Proposed)RecordBuilding.Build(typeof(Proposed), new Dictionary<string, string>
        {
            ["OwnerId"] = Owner.ToString(), ["Reason"] = "",
        }, partial: true);

        built.OwnerId.ShouldBe(Owner);
    }

    [Fact]
    public void a_defaulted_parameter_still_wins_over_the_partial_default()
    {
        // The #177 path is unchanged: an explicit C# default is a value the author chose, and it
        // must still beat default(T).
        var built = (Sized)RecordBuilding.Build(typeof(Sized), new Dictionary<string, string>
        {
            ["Name"] = "n",
        }, partial: true);

        built.Count.ShouldBe(3);
    }

    private record Sized(string Name, int Count = 3);
}
