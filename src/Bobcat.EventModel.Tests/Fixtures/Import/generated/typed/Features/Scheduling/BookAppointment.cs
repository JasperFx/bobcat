// Imported from an event model by `bobcat import-event-model`.
//
// A stub has only the fields the model names, typed from what it declares or from its
// example values, plus an Id where the model names no identity. Add the rest as the
// behaviour takes shape. Nothing regenerates this file, so your edits are safe.

#nullable enable

using System;
using System.Collections.Generic;
using JasperFx;

namespace Clinic.Scheduling;

/// <summary>
/// Books one visit for a patient.
/// the command of BookAppointment.
/// </summary>
public record BookAppointment([Generated] Guid AppointmentId, [Pii] string PatientEmail, List<int> Slots, string? Notes, Window Window);

/// <summary>The Window of BookAppointment, as the model declares it.</summary>
public record Window(DateOnly Day, DateTimeOffset From, bool Flexible);

/// <summary>
/// emitted by BookAppointment.
/// consumed by AppointmentList.
/// </summary>
public record AppointmentBooked(Guid AppointmentId, [Pii] string PatientEmail, [Generated] DateTimeOffset BookedAt, decimal? Deposit);
