using System.Globalization;
using Ehr.Data;
using Ehr.Data.Lab;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Results;

// Filing the results of a lab order and acknowledging them (the expansion plan's step 7). A clinician with the patient
// on their panel types in the values the lab reported; a connected lab files them itself through its door (step 12,
// FileFromLabAsync), under the same rules. This class checks what was typed and sends the rows. Everything that
// matters is the database's (db/policies/30-rls.sql, 75-results.sql and 76-lab.sql): who may file, that each result
// answers one of the order's tests with the database's own name, unit and reference range, that the order becomes
// completed and its signer is told, and who acknowledged and when. It reads nothing about a patient: the page's lists
// come from the PHI reader.
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
                errors[code] = "Result must be a number.";
            }
        }

        if (errors.Count == 0 && values.Count == 0)
        {
            errors[""] = "At least one result is required.";
        }

        if (collectedAt is null)
        {
            errors["CollectedAt"] = "Sample collected is required.";
        }
        else if (collectedAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            errors["CollectedAt"] = "Sample collected must not be in the future.";
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

    // Files a lab's results for one order through its door, all or none, by the hash of the lab's key: what the
    // database did with them (db/policies/76-lab.sql). Nobody is signed in; the database records the lab as the source.
    public async Task<LabFiling> FileFromLabAsync(
        string keyHash, Guid order, DateTimeOffset collectedAt, IReadOnlyList<(string Code, decimal Value, string Unit)> values, CancellationToken cancellationToken) =>
        await LabStore.FileAsync(db, keyHash, order, collectedAt, values, cancellationToken) switch
        {
            "filed" => LabFiling.Filed,
            "already-filed" => LabFiling.AlreadyFiled,
            "malformed" => LabFiling.Malformed,
            "unknown-order" => LabFiling.UnknownOrder,
            "discontinued" => LabFiling.Discontinued,
            "closed" => LabFiling.Closed,
            "not-reported" => LabFiling.NotReported,
            "wrong-unit" => LabFiling.WrongUnit,
            "in-the-future" => LabFiling.InTheFuture,
            "different-result" => LabFiling.DifferentResult,
            var other => throw new InvalidOperationException($"The lab's door answered \"{other}\", which this version doesn't know."),
        };

    // Whether a lab is connected, which then files results itself: the results page says so (step 12). Not about any
    // patient; both servers can answer.
    public Task<bool> LabConnectedAsync(CancellationToken cancellationToken) => LabStore.ConnectedAsync(db, cancellationToken);

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

// What the database did with a lab's results (db/policies/76-lab.sql, ehr.file_lab_results): filed, or found on file
// already exactly as sent; or nothing, and why.
public enum LabFiling
{
    Filed,
    AlreadyFiled,
    Malformed,
    UnknownOrder,
    Discontinued,
    Closed,
    NotReported,
    WrongUnit,
    InTheFuture,
    DifferentResult,
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
    // The patient's earlier and present values of the tests this order reports, for their trends (step 9).
    public IReadOnlyList<Trend> Trends { get; init; } = [];

    public bool HasResults => Tests.Any(test => test.Value is not null);

    // What a page showing these results has shown: how many, and when the latest was released. An acknowledgment
    // carries it back, so it never covers a result that arrived after the page was loaded.
    public string Seen => $"{Tests.Count(test => test.Value is not null)}-{Tests.Max(test => test.ReleasedAt)?.UtcTicks ?? 0}";

    // Results can still be added: a lab order, not discontinued, not acknowledged, with a test still waiting.
    public bool OpenToResults => Type == OrderType.Lab && Status is OrderStatus.Active or OrderStatus.Completed && AcknowledgedAt is null && Tests.Any(test => test.Value is null);
}
