// Imported from an eventmodelers.ai board by `bobcat import-event-model`.
//
// This is ordinary C# and nothing regenerates it. The segmentation an import performs is a
// set of reported guesses, so a wrong guess is a one-line edit here rather than a re-import.

using JasperFx.Events.EventModeling;
using Shelter;

namespace Shelter.Booking;

public class BookingModel : EventModelDefinition
{
    public override void Configure(EventModelBuilder model)
    {
        model.InChapter("Booking");
        model.Aggregate<Appointment>();

        // From chapter 'Booking', actor 'Member'.
        // ⚠ inferred: starts the Appointment stream — no example gives it an earlier Appointment event; 'Appointment' is the subject 2 of its 2 events share.
        model.Command<BookAppointment>()
            .StartsStream<Appointment>()
            .Emits<AppointmentBooked>()
            .LinksToSpecification("BookAppointment/booked");

        // From chapter 'Booking', actor 'Staff'.
        // ⚠ inferred: decides against Appointment — its examples give Appointment events before it appends; 'Appointment' is the subject 2 of its 2 events share.
        model.Command<RecordAppointmentNoShow>()
            .Against<Appointment>()
            .Emits<AppointmentMissed>()
            .LinksToSpecification("RecordAppointmentNoShow/missed");

        model.Slice("RecordAppointmentNoShow").Emits<AppointmentNoShowRecorded>();
    }
}
