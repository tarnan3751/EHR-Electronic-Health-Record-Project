using System.Globalization;
using Ehr.Data;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Results;

// Filing the results of a lab order and acknowledging them (the expansion plan's step 7). A clinician with the patient
// on their panel types in the values the lab reported; the simulated lab will file them the same way (step 12). This
// class checks what was typed and sends the rows. Everything that matters is the database's (db/policies/30-rls.sql
// and 75-results.sql): who may file, that each result answers one of the order's tests with the database's own name,
// unit and reference range, that the order becomes completed and its signer is told, and who acknowledged and when.
// It reads nothing: the page's lists come from the PHI reader.
public sealed class LabResults(EhrDbContext db)
{
    // What is wrong with what was typed, by test code ("" for the whole form): empty when it can be sent.
    public static Dictionary<string, string> Check(IReadOnlyDictionary<string, string?> typed, DateTimeOffset? collectedAt, out List<(string Code, decimal Value)> values)
    {
        var errors = new Dictionary<string, string>();
        values = [];
        foreach (var (code, text) in typed)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                && value is >= 0 and < 100_000_000 && decimal.Round(value, 4) == value)
            {
                values.Add((code, value));
            }
            else
            {
                errors[code] = "Enter a number, like 5.4.";
            }
        }

        if (errors.Count == 0 && values.Count == 0)
        {
            errors[""] = "Enter at least one result.";
        }

        if (collectedAt is null)
        {
            errors["CollectedAt"] = "Enter when the sample was collected.";
        }
        else if (collectedAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            errors["CollectedAt"] = "The collection time can't be in the future.";
        }

        return errors;
    }

    // Files the values against the order, all or none. False when the database refused: the order isn't an open lab
    // order of this patient, a test isn't one it reports or already has a result, or the signed-in user may not.
    public async Task<bool> FileAsync(Guid patient, Guid order, IReadOnlyList<(string Code, decimal Value)> values, DateTimeOffset collectedAt, CancellationToken cancellationToken)
    {
        // As objects: the rows are written, never read here (PhiReaderTests). The database sets the name, the unit,
        // the range, the status and the release time.
        var rows = values.Select(value => (object)new Observation
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            OrderId = order,
            Code = value.Code,
            Display = value.Code,
            Status = ObservationStatus.Final,
            Value = value.Value,
            EffectiveAt = collectedAt,
        }).ToArray();
        return await SaveAsync("results", rows, cancellationToken);
    }

    // Records that the signed-in clinician has seen the order's results. False when the database refused: the order
    // has no results, was acknowledged already, or the patient isn't on their panel.
    public Task<bool> AcknowledgeAsync(Guid patient, Guid order, CancellationToken cancellationToken) =>
        SaveAsync("acknowledgment", [new ResultAcknowledgment { Id = Guid.CreateVersion7(), OrderId = order, PatientId = patient }], cancellationToken);

    async Task<bool> SaveAsync(string savepoint, object[] rows, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        db.AddRange(rows);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation
                or PostgresErrorCodes.ForeignKeyViolation,
        })
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
        }
        finally
        {
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }
}

// A completed lab order as the chart lists it: when its latest result was released, how many of its results are
// outside their reference range, and whether anyone has acknowledged them.
public sealed record ResultedOrder(Guid Id, string Display, DateTimeOffset? ResultedAt, int Abnormal, bool Acknowledged);

// One test an order reports, with its result when one has been filed.
public sealed record OrderTest(
    string Code, string Display, string? Unit, decimal? Low, decimal? High, decimal? Value, DateTimeOffset? CollectedAt, DateTimeOffset? ReleasedAt)
{
    // "High" or "Low" when the value is outside the reference range; null inside it, without one, or without a value.
    public string? Flag => Value is null ? null : High is not null && Value > High ? "High" : Low is not null && Value < Low ? "Low" : null;

    public string Range => (Low, High) switch
    {
        (not null, not null) => $"{Low:0.####} to {High:0.####}",
        (not null, null) => $"at least {Low:0.####}",
        (null, not null) => $"at most {High:0.####}",
        _ => "",
    };
}

// An order with the tests it reports, their results so far, and its acknowledgment: one recorded read of the patient.
public sealed record OrderResults(
    Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, Guid OrderId, string Display, string Type, string Status, string SignedBy, DateTimeOffset SignedAt,
    IReadOnlyList<OrderTest> Tests, string? AcknowledgedBy, DateTimeOffset? AcknowledgedAt)
{
    public bool HasResults => Tests.Any(test => test.Value is not null);

    // Results can still be added: a lab order, not discontinued, not acknowledged, with a test still waiting.
    public bool OpenToResults => Type == OrderType.Lab && Status is OrderStatus.Active or OrderStatus.Completed && AcknowledgedAt is null && Tests.Any(test => test.Value is null);
}
