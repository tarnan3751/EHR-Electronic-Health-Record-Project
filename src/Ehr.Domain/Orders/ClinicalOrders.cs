using Ehr.Data;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Orders;

// UC 1 Place an order (Tahseen Arnan). Called inside the page's database transaction (DatabaseTransactionFilter). The
// catalog isn't PHI, so it is read here; the patient and their orders are read through PhiReader. Who may sign is the
// order page's rule and the database's (a clinician, as themselves, for a patient on their panel: db/policies/30-rls.sql),
// and the database also sets the time, records the write, and refuses a duplicate without a reason (60-orders.sql). A
// signed order is never edited: it is discontinued, with a reason, or changed, which signs a new order and discontinues
// the old one as replaced.
public sealed partial class ClinicalOrders(EhrDbContext db)
{
    // The savepoint the insert runs in, so the request's transaction outlives a refused duplicate.
    const string Savepoint = "sign_order";

    // How many items the search offers as a clinician types.
    public const int Suggestions = 20;

    // The clinic's common items of a type, which the order form lists without being asked. The rest of the catalog,
    // LOINC's lab orders and RxNorm's clinical drugs, is thousands of items: the form finds those with SearchAsync.
    public async Task<IReadOnlyList<OrderCatalogItem>> CatalogAsync(string type, CancellationToken cancellationToken) =>
        await db.OrderCatalogItems.AsNoTracking().Where(i => i.Type == type && i.Common).OrderBy(i => i.Display).ToListAsync(cancellationToken);

    // Items of a type that match what was typed: the common ones first, then names that start with it, then names
    // with words that start with each word typed. Each is an index lookup (OrderCatalogItemConfiguration). Nothing for
    // fewer than two characters.
    public async Task<IReadOnlyList<OrderCatalogItem>> SearchAsync(string type, string? typed, CancellationToken cancellationToken)
    {
        var key = typed?.Trim().ToLowerInvariant() ?? "";
        if (key.Length < 2 || !OrderType.All.Contains(type))
        {
            return [];
        }

        var items = db.OrderCatalogItems.AsNoTracking().Where(i => i.Type == type);
        var pattern = key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
        var found = await items.Where(i => i.Common && EF.Functions.Like(i.DisplayKey, "%" + pattern + "%"))
            .OrderBy(i => i.DisplayKey).Take(Suggestions).ToListAsync(cancellationToken);
        Add(await items.Where(i => EF.Functions.Like(i.DisplayKey, pattern + "%"))
            .OrderBy(i => i.DisplayKey).Take(Suggestions).ToListAsync(cancellationToken));
        // Words of letters and digits only, so nothing typed can be read as the query's own syntax.
        var words = Word().Matches(key).Select(match => match.Value).Where(word => word.Length > 1).Take(6).ToList();
        if (found.Count < Suggestions && words.Count > 0)
        {
            var query = string.Join(" & ", words.Select(word => word + ":*"));
            Add(await items.Where(i => EF.Functions.ToTsVector("simple", i.Display).Matches(EF.Functions.ToTsQuery("simple", query)))
                .OrderBy(i => i.DisplayKey.Length).ThenBy(i => i.DisplayKey).Take(Suggestions).ToListAsync(cancellationToken));
        }

        return found;

        void Add(List<OrderCatalogItem> more) =>
            found.AddRange(more.Where(item => found.All(have => have.Code != item.Code)).Take(Suggestions - found.Count));
    }

    [System.Text.RegularExpressions.GeneratedRegex("[a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex Word();

    // What is wrong with an order before it goes to the database, by field: "Item", or a detail's name. The item is the
    // catalog's, as the clinician picked or typed it, in any case.
    public async Task<(OrderCatalogItem? Item, Dictionary<string, string> Errors)> CheckAsync(
        string type, string? item, IReadOnlyDictionary<string, string?> details, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>();
        var display = item?.Trim().ToLowerInvariant();
        var found = string.IsNullOrEmpty(display)
            ? null
            : await db.OrderCatalogItems.AsNoTracking().SingleOrDefaultAsync(i => i.Type == type && i.DisplayKey == display, cancellationToken);
        if (found is null)
        {
            errors["Item"] = string.IsNullOrWhiteSpace(item) ? "Item is required." : "Item must be selected from the list.";
        }

        foreach (var field in OrderForms.Fields[type])
        {
            var value = details.GetValueOrDefault(field.Name);
            if (field.Required && string.IsNullOrWhiteSpace(value))
            {
                errors[field.Name] = $"{field.Label} is required.";
            }
            else if (value?.Trim().Length > OrderForms.MaximumLength)
            {
                errors[field.Name] = $"{field.Label} must be {OrderForms.MaximumLength} characters or less.";
            }
        }

        return (found, errors);
    }

    // Signs the order: the database sets the signer and the time. A blank reason is none. Duplicate when the database
    // refused it as a duplicate without a reason, one signed at the same moment as another; the order page then warns as
    // for any duplicate. Only in the page's own transaction, READ COMMITTED, which the signing trigger requires.
    //
    // A change (`replaces`) signs the new order and discontinues the one it replaces, for `changeReason`, in one
    // savepoint: both happen or neither does, and the database checks that again when the transaction commits. Gone
    // when the order to replace is no longer active, because someone discontinued or changed it first; nothing is saved.
    //
    // Approving a refill request (`refill`, step 13d) signs the order and answers the request with it, in the same
    // savepoint: the database holds the answer to an order signed now, by this clinician, for the same medication product,
    // while the request waits and the medication is active (77-refill-requests.sql). Answered when someone answered the request
    // first, NotRenewable when its medication is no longer active or the order isn't one that renews it; nothing is
    // saved then, not the order either.
    //
    // An order the clinician's dictation proposed (`proposal`, step 15) is signed as any order is, every rule and check
    // with it, and decides the proposal, in the same savepoint: the database holds the proposal to an order signed now,
    // by this clinician (55-ai-review.sql). ProposalDecided when the proposal was decided first; nothing is saved then,
    // not the order either.
    public async Task<(Order? Order, SignRefusal? Refusal)> SignAsync(
        Guid patient, OrderCatalogItem item, string details, string? duplicateReason, CancellationToken cancellationToken,
        Guid? replaces = null, string? changeReason = null, string? safetyReason = null, Guid? refill = null, Guid? proposal = null)
    {
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            Type = item.Type,
            CatalogCode = item.Code,
            Details = details,
            Status = OrderStatus.Active,
            DuplicateReason = string.IsNullOrWhiteSpace(duplicateReason) ? null : duplicateReason.Trim(),
            // Kept only when the database finds something against the order (60-orders.sql); required then.
            SafetyOverrideReason = string.IsNullOrWhiteSpace(safetyReason) ? null : safetyReason.Trim(),
            ReplacesOrderId = replaces,
        };
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
        db.AddRange(order);
        SignRefusal? refusal = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (replaces is { } replaced && !await UpdateAsync(replaced, patient, item.Type, changeReason ?? ReplacedReason, order.Id, cancellationToken))
            {
                refusal = SignRefusal.Gone;
            }

            if (refusal is null && refill is { } request)
            {
                refusal = await AnswerRefillAsync(request, patient, order.Id, cancellationToken);
            }

            if (refusal is null && proposal is { } proposed)
            {
                refusal = await DecideProposalAsync(proposed, patient, order.Id, cancellationToken);
            }
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            refusal = SignRefusal.Duplicate;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "orders_safety_override" })
        {
            // The signing trigger found something against the order and no reason came with it: a warning that
            // appeared between the form and the signing, such as a medication a colleague just added.
            refusal = SignRefusal.Unsafe;
        }
        catch (DbUpdateException e) when (replaces is not null && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            // The signing trigger: the order to replace isn't an active order of this patient and type any more.
            refusal = SignRefusal.Gone;
        }

        db.Entry((object)order).State = EntityState.Detached;
        if (refusal is not null)
        {
            await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
            return (null, refusal);
        }

        await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
        return (order, null);
    }

    // Answers a refill request with the order just signed for it: the one column the apps may write for an approval.
    // Null when it was answered; why not otherwise.
    async Task<SignRefusal?> AnswerRefillAsync(Guid request, Guid patient, Guid order, CancellationToken cancellationToken)
    {
        // The medication's locks first, then the request's row, in the order marking the medication sensitive takes them:
        // the other way round, the two could each wait for the other.
        await RefillRequestQueries.LockMedicationAsync(db, request, cancellationToken);
        // A stand-in for the row, with its key: only order_id is sent.
        var row = new RefillRequest { Id = request, PatientId = patient, Status = RefillRequestStatus.Requested };
        var entry = db.Attach((object)row);
        row.OrderId = order;
        entry.Property(nameof(RefillRequest.OrderId)).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row this clinician may answer.
            return SignRefusal.NotRenewable;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation } refusal)
        {
            return refusal.ConstraintName == "refill_requests_answered" ? SignRefusal.Answered : SignRefusal.NotRenewable;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    // Decides the dictation's proposal with the order just signed for it (step 15): the two columns the apps may write for
    // it, on the row only while it waits. Null when decided; ProposalDecided when it was decided first.
    async Task<SignRefusal?> DecideProposalAsync(Guid proposal, Guid patient, Guid order, CancellationToken cancellationToken)
    {
        // A stand-in for the row, with its key and the state it must still be in: only the decision is sent.
        var row = new AiChartProposal { Id = proposal, PatientId = patient, Kind = AiChartProposalKinds.Order, State = AiChartProposalStates.Pending };
        var entry = db.Attach((object)row);
        row.State = AiChartProposalStates.Ordered;
        row.OrderId = order;
        entry.Property(nameof(AiChartProposal.State)).IsModified = true;
        entry.Property(nameof(AiChartProposal.OrderId)).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No waiting proposal this clinician may decide: decided first, in another tab.
            return SignRefusal.ProposalDecided;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    // What a replaced order's reason says when the clinician gave none.
    public const string ReplacedReason = "Replaced by a changed order";

    // Discontinues an active order, with the reason: the database sets who and when, and holds the update to that one
    // change (db/policies/60-orders.sql). False when the order is no longer active, or isn't one this clinician may
    // discontinue; nothing is saved then. Only in the page's own transaction.
    public async Task<bool> DiscontinueAsync(Guid order, Guid patient, string type, string reason, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
        if (await UpdateAsync(order, patient, type, reason, replacedBy: null, cancellationToken))
        {
            await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
            return true;
        }

        await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
        return false;
    }

    // The one update of an order there is: active to revoked, with the reason and, for a change, the order that
    // replaced it. Writes those three columns of the row with this ID and reads nothing. The patient and type only
    // fill the stand-in entity: they aren't sent and aren't checked here. The caller already read the order as this
    // patient's through PhiReader, and row-level security and the trigger decide whether the update may happen.
    // False when it didn't.
    async Task<bool> UpdateAsync(Guid id, Guid patient, string type, string reason, Guid? replacedBy, CancellationToken cancellationToken)
    {
        // A stand-in for the row, with its key: only the properties marked below are sent.
        var order = new Order { Id = id, PatientId = patient, Type = type, CatalogCode = "", Details = "{}", Status = OrderStatus.Active };
        var entry = db.Attach((object)order);
        order.Status = OrderStatus.Revoked;
        order.DiscontinueReason = reason.Trim();
        order.ReplacedByOrderId = replacedBy;
        entry.Property(nameof(Order.Status)).IsModified = true;
        entry.Property(nameof(Order.DiscontinueReason)).IsModified = true;
        entry.Property(nameof(Order.ReplacedByOrderId)).IsModified = replacedBy is not null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row: not an order this clinician may discontinue.
            return false;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
        {
            // The trigger: the order isn't active any more.
            return false;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}

// Why an order wasn't signed.
public enum SignRefusal
{
    // The patient has an active order for the item, and no reason was given.
    Duplicate,

    // The order a change replaces is no longer active.
    Gone,

    // The database found a safety warning against the order, and no reason to sign it all the same was given.
    Unsafe,

    // The refill request the order approves was answered by someone else first (step 13d).
    Answered,

    // The refill request's medication is no longer active, or the order doesn't renew it (step 13d).
    NotRenewable,

    // The dictation's proposal the order was signed for was decided first, in another tab (step 15).
    ProposalDecided,
}
