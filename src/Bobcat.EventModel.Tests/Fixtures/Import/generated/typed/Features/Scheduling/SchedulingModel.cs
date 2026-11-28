// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using System;
using JasperFx.Events.EventModeling;
using Clinic;

namespace Clinic.Scheduling;

public class SchedulingModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("Scheduling");
        model.Aggregate<Appointment>();

        // From chapter 'Book Appointment', actor ''.
        // A patient books a visit from the schedule page.
        model.Command<BookAppointment>()
            .TriggeredBy("Schedule Page", TriggerKind.Human)
            .StartsStream<Appointment>()
            .Emits<AppointmentBooked>()
            .OnStream<Appointment>()
            .LinksToSpecification("BookAppointment/books a visit")
            .Describe("A patient books a visit from the schedule page.")
            .Describe<BookAppointment>("Books one visit for a patient.");

        // From chapter 'Appointment List', actor ''.
        model.View<AppointmentList>()
            .On<AppointmentBooked>()
            .Describe<AppointmentList>("Every booked visit, for the front desk.");

        // #region shapes
        // The fields the board declares, so the model carries them before the code does (jasperfx#1299).
        // Once a type has members, what the code says wins; a field declared here that the code lacks is a hotspot.
        model.Shape<Appointment>()
            .Field("Id").OfType<Guid>().Identity();

        model.Shape<BookAppointment>()
            .Field("AppointmentId").OfType<Guid>().Identity().Generated()
            .Field("PatientEmail").OfType<string>().Pii()
            .Field("Slots").OfType<int>().Many()
            .Field("Notes").OfType<string>().Optional()
            .Field("Window").OfType<Window>();

        model.Shape<Window>()
            .Field("Day").OfType<DateOnly>()
            .Field("From").OfType<DateTimeOffset>()
            .Field("Flexible").OfType<bool>();

        model.Shape<AppointmentBooked>()
            .Field("AppointmentId").OfType<Guid>().Identity()
            .Field("PatientEmail").OfType<string>().Pii()
            .Field("BookedAt").OfType<DateTimeOffset>().Generated()
            .Field("Deposit").OfType<decimal>().Optional();

        model.Shape<AppointmentList>()
            .Field("AppointmentId").OfType<Guid>().Identity()
            .Field("Email").OfType<string>().Pii().From<AppointmentBooked>("PatientEmail")
            .Field("Visits").OfType<long>();
        // #endregion shapes
    }
}
