using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Ehr.Data.Scheduling;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Scheduling;

// Booking and changing appointments (FR-17; the expansion plan's step 4). Called inside the page's database
// transaction. Appointments and patients are read through PhiReader; the clinicians one can be booked with are staff
// accounts, not PHI, and are read here. Who may write is the page's rule and the database's (a clinician, for a
// patient on their panel: db/policies/30-rls.sql); the database also holds each change to one of four moves, refuses a
// booking that overlaps another of the same clinician, and records the write (80-appointments.sql).
public sealed class Appointments(EhrDbContext db)
{
    public const int MaximumLength = 200;

    public const int MaximumReasonLength = 500;

    // The lengths the forms offer, in minutes.
    public static readonly int[] Lengths = [10, 15, 20, 30, 45, 60];

    // The clinicians a patient can be booked with: every clinician account that isn't disabled.
    public async Task<IReadOnlyList<(Guid Id, string Name)>> CliniciansAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var clinicians = await db.StaffUsers.AsNoTracking()
            // Not disabled: a disabled account is locked out for good, one locked for minutes after wrong passwords isn't.
            .Where(s => s.Roles.Contains(StaffRoles.Clinician) && (s.LockoutEnd == null || s.LockoutEnd < now.AddYears(10)))
            .OrderBy(s => s.DisplayName).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.DisplayName })
            .ToListAsync(cancellationToken);
        return [.. clinicians.Select(c => (c.Id, c.DisplayName))];
    }

    public Task<AppointmentResult> BookAsync(Guid patient, Guid clinician, DateTimeOffset startsAt, int minutes, string reason, CancellationToken cancellationToken) =>
        SaveAsync(new Appointment
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            ClinicianId = clinician,
            StartsAt = startsAt,
            Minutes = minutes,
            Reason = reason.Trim(),
            Status = AppointmentStatus.Booked,
        }, isNew: true, cancellationToken);

    // Each change names the version of the appointment the page showed: a change that began from an older one, because
    // a colleague rescheduled in between, is refused and saves nothing.
    public Task<AppointmentResult> RescheduleAsync(Guid id, int version, DateTimeOffset startsAt, int minutes, string reason, CancellationToken cancellationToken) =>
        SaveAsync(Stub(id, version, AppointmentStatus.Booked, a =>
        {
            a.StartsAt = startsAt;
            a.Minutes = minutes;
            a.Reason = reason.Trim();
        }), isNew: false, cancellationToken, nameof(Appointment.StartsAt), nameof(Appointment.Minutes), nameof(Appointment.Reason));

    public Task<AppointmentResult> CancelAsync(Guid id, int version, string reason, CancellationToken cancellationToken) =>
        SaveAsync(Stub(id, version, AppointmentStatus.Cancelled, a => a.CancelReason = reason.Trim()), isNew: false, cancellationToken,
            nameof(Appointment.Status), nameof(Appointment.CancelReason));

    public Task<AppointmentResult> NoShowAsync(Guid id, int version, CancellationToken cancellationToken) =>
        SaveAsync(Stub(id, version, AppointmentStatus.NoShow, _ => { }), isNew: false, cancellationToken, nameof(Appointment.Status));

    // The patient has arrived: their visit starts (an encounter, in progress, for the appointment's reason) and the
    // appointment names it. Both, or neither.
    public async Task<AppointmentResult> CheckInAsync(Guid id, int version, Guid patient, string reason, CancellationToken cancellationToken)
    {
        const string savepoint = "check_in";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        // The start sent is the app's clock; the database replaces it with its own, the moment the row is inserted
        // (db/policies/75-results.sql, ehr.check_in_visit). Nothing here reads it back: the visit is detached below, and
        // the pages read the visit from the database.
        var visit = new Encounter
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            Status = EncounterStatus.InProgress,
            ClassCode = EncounterClass.Ambulatory,
            Reason = reason,
            PeriodStart = DateTimeOffset.UtcNow,
        };
        var entry = db.Add((object)visit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            // Refused by row-level security (the patient isn't on the panel), or by the database's rule for a visit
            // the apps start, which the page's visit always meets.
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation,
        })
        {
            entry.State = EntityState.Detached;
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return AppointmentResult.Refused;
        }

        entry.State = EntityState.Detached;
        var result = await SaveAsync(Stub(id, version, AppointmentStatus.Fulfilled, a => a.EncounterId = visit.Id), isNew: false, cancellationToken,
            nameof(Appointment.Status), nameof(Appointment.EncounterId));
        if (result != AppointmentResult.Saved)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return result;
        }

        await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        return result;
    }

    // A stand-in for the row, with its key and the version the change began from: only the properties a move names are
    // sent, and only to that version of the row.
    static Appointment Stub(Guid id, int version, string status, Action<Appointment> change)
    {
        var appointment = new Appointment { Id = id, Version = version, Reason = "", Status = status };
        change(appointment);
        return appointment;
    }

    // An insert, or an update of the named columns of the row with the appointment's ID. Reads nothing: the page read
    // the appointment through PhiReader, and row-level security and the trigger decide whether the write may happen.
    async Task<AppointmentResult> SaveAsync(Appointment appointment, bool isNew, CancellationToken cancellationToken, params string[] changed)
    {
        if (appointment.Id == Guid.Empty)
        {
            return AppointmentResult.Refused;
        }

        const string savepoint = "appointment";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var entry = isNew ? db.Add((object)appointment) : db.Attach((object)appointment);
        foreach (var property in isNew ? [] : changed)
        {
            entry.Property(property).IsModified = true;
        }

        var result = AppointmentResult.Saved;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row: not an appointment this clinician may change, or changed by someone else since the page was read.
            result = AppointmentResult.Refused;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            result = AppointmentResult.Overlap;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            // Refused by row-level security, or by the trigger: no longer booked, or not with a clinician.
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation,
        })
        {
            result = AppointmentResult.Refused;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }

        if (result == AppointmentResult.Saved)
        {
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        }
        else
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
        }

        return result;
    }
}

public enum AppointmentResult
{
    Saved,

    // The clinician already has an appointment booked then.
    Overlap,

    // Not this clinician's to change, or no longer booked.
    Refused,
}

// An appointment as the schedule, the chart and the portal list it: only the fields they show.
public sealed record AppointmentEntry(
    Guid Id, Guid PatientId, string? PatientName, string? PatientMrn, Guid ClinicianId, string Clinician, DateTimeOffset StartsAt, int Minutes, string Reason,
    string Status, string? CancelReason, int Version = 0);
