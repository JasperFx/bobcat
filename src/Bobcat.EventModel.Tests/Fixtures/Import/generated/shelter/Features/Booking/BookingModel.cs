// #region booking-model
// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using JasperFx.Events.EventModeling;
using Shelter;
using Shelter.Volunteering;

namespace Shelter.Booking;

public class BookingModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("Booking");
        // ⚠ inferred: 2 of the 2 commands here decide against Appointment, so it is the
        // default; .Against<T>() on a slice replaces it, and .NoAggregate() says it has none.
        model.ForAggregate<Appointment>();

        // From chapter 'Booking', actor 'System'.
        // ⚠ inferred: starts the Appointment stream — no example gives it an earlier Appointment event; 'Appointment' is the subject 3 of its 3 events share.
        model.Automation("ProposeHomeCheckAppointment")
            .TriggeredBy(TriggerKind.MessageHandler)
            .Command<ProposeHomeCheckAppointment>()
            .StartsStream<Appointment>()
            .Emits<HomeCheckAppointmentProposed>()
            .LinksToSpecification("ProposeHomeCheckAppointment/home check proposed");

        // From chapter 'Booking', actor 'System'.
        // ⚠ inferred: starts the Appointment stream — no example gives it an earlier Appointment event; 'Appointment' is the subject 3 of its 3 events share.
        model.Automation("ProposeFosterHandoverAppointment")
            .TriggeredBy(TriggerKind.MessageHandler)
            .Command<ProposeFosterHandoverAppointment>()
            .StartsStream<Appointment>()
            .Emits<FosterHandoverAppointmentProposed>()
            .LinksToSpecification("ProposeFosterHandoverAppointment/foster proposed");

        // #region confirm-appointment
        // From chapter 'Booking', actor 'Member'.
        // ⚠ inferred: decides against Appointment — its examples give Appointment events before it appends; 'Appointment' is the subject 3 of its 3 events share.
        model.Command<ConfirmAppointment>()
            .Emits<AppointmentConfirmed>()
            .LinksToSpecification("ConfirmAppointment/confirmed");
        // #endregion confirm-appointment

        // From chapter 'Booking', actor 'Staff'.
        // ⚠ inferred: decides against Appointment — its examples give Appointment events before it appends; 'Appointment' is the subject 3 of its 3 events share.
        model.Command<CancelAppointment>()
            .Emits<AppointmentCancelled>()
            .LinksToSpecification("CancelAppointment/cancelled");
    }
}
// #endregion booking-model
