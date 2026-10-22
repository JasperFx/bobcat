// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using JasperFx.Events.EventModeling;
using Shelter;
using Shelter.Booking;

namespace Shelter.Volunteering;

public class VolunteeringModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("Volunteering");
        // ⚠ inferred: 3 of the 5 commands here decide against VolunteerApplication, so it is the
        // default; .Against<T>() on a slice replaces it, and .NoAggregate() says it has none.
        model.ForAggregate<VolunteerApplication>();
        model.Aggregate<HomeCheck>();

        // From chapter 'Volunteering', actor 'Volunteer'.
        // ⚠ inferred: starts the VolunteerApplication stream — no example gives it an earlier VolunteerApplication event; 'VolunteerApplication' is the subject 3 of its 4 events share.
        model.Command<ApplyToVolunteer>()
            .StartsStream<VolunteerApplication>()
            .Emits<VolunteerApplicationSubmitted>()
            .LinksToSpecification("ApplyToVolunteer/submitted");

        // From chapter 'Volunteering', actor 'Admin'.
        // ⚠ inferred: decides against VolunteerApplication — its examples give VolunteerApplication events before it appends; 'VolunteerApplication' is the subject 3 of its 4 events share.
        model.Command<ReviewVolunteerApplication>()
            .Emits<VolunteerApplicationReviewed>()
            .LinksToSpecification("ReviewVolunteerApplication/reviewed");

        // From chapter 'Volunteering', actor 'Admin'.
        // ⚠ inferred: decides against VolunteerApplication — its examples give VolunteerApplication events before it appends; 'VolunteerApplication' is the subject 3 of its 4 events share.
        model.Command<ApproveVolunteer>()
            .Emits<VolunteerApproved>()
            .LinksToSpecification("ApproveVolunteer/approved");

        // From chapter 'Volunteering', actor 'Admin'.
        // ⚠ inferred: decides against VolunteerApplication — its examples give VolunteerApplication events before it appends; 'VolunteerApplication' is the subject 3 of its 4 events share.
        model.Command<RejectVolunteerApplication>()
            .Emits<VolunteerApplicationRejected>()
            .LinksToSpecification("RejectVolunteerApplication/rejected");

        // From chapter 'Volunteering', actor 'Staff'.
        // ⚠ starts the HomeCheck stream? It is inferred to decide against HomeCheck, but the only HomeCheck events its examples give first, ApplicationReviewed, come from chapter 'ShelterReviewsApplication', which was not imported. If it starts the stream, import with --starts-stream RequestHomeCheck=HomeCheck.
        model.Command<RequestHomeCheck>()
            .Against<HomeCheck>()
            .Emits<HomeCheckRequested>()
            .LinksToSpecification("RequestHomeCheck/requested");

        // From chapter 'Volunteering', actor 'Volunteer'.
        // ⚠ inferred: decides against HomeCheck — its examples give HomeCheck events before it appends; 'HomeCheck' is the subject 2 of its 3 events share.
        // ⚠ inferred: decides against VolunteerApplication — its examples give VolunteerApproved from the VolunteerApplication stream; 'VolunteerApplication' is the subject 3 of its 4 events share.
        // ⚠ draws on several streams (HomeCheck, VolunteerApplication): choose several [WriteModel] IEventStream<T> parameters or a DCB decider (bobcat#443).
        model.Command<AcceptHomeCheckAssignment>()
            .Against<HomeCheck>()
            .Against<VolunteerApplication>()
            .Emits<HomeCheckAssignmentAccepted>()
            .LinksToSpecification("AcceptHomeCheckAssignment/accepted");
    }
}
