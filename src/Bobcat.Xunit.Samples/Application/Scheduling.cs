namespace Bobcat.Xunit.Samples.Application;

/// <summary>
/// The system under test for the slice-binding and placeholder examples on <c>docs/marker-steps.md</c>:
/// home-check appointments a volunteer proposes and confirms. An in-memory stand-in for an HTTP endpoint
/// over an event store, so the page's specifications run without hosting anything.
/// </summary>
public record Appointment;

public record AppointmentProposed(Guid Id, string Volunteer);
public record AppointmentConfirmed(Guid Id);
public record AppointmentCancelled(Guid Id);

public record ConfirmAppointment(Guid Id);
public record ProposeHomeCheck(Guid Id, string Volunteer);

public class SchedulingApp
{
    private readonly Dictionary<Guid, List<object>> _streams = new();

    public List<object> Emitted { get; } = new();

    public void Append(Guid id, IEnumerable<object> events)
    {
        if (!_streams.TryGetValue(id, out var stream)) _streams[id] = stream = new();
        stream.AddRange(events);
    }

    /// <summary>POST a command, and answer with a status code the way the endpoint would.</summary>
    public int Post(object command, string route)
    {
        Emitted.Clear();

        switch (command)
        {
            case ConfirmAppointment confirm:
                if (!_streams.TryGetValue(confirm.Id, out var stream)) return 404;
                if (stream.OfType<AppointmentConfirmed>().Any()) return 409;

                // Not finished: a cancelled appointment is confirmed anyway. BookingSpecs' Acceptance spec says so
                emit(confirm.Id, new AppointmentConfirmed(confirm.Id));
                return 200;

            case ProposeHomeCheck propose:
                emit(propose.Id, new AppointmentProposed(propose.Id, propose.Volunteer));
                return 201;

            default:
                return 400;
        }
    }

    private void emit(Guid id, object @event)
    {
        Append(id, [@event]);
        Emitted.Add(@event);
    }
}
