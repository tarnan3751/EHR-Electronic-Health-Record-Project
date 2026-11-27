using Ehr.Data;
using Ehr.Domain.Orders;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests;

// Approving a refill request (step 13d), on the second review's findings that the test cases don't cover.
//
// Approving through ClinicalOrders.SignAsync, as the order form does, while a colleague marks the medication sensitive.
// Marking takes the medication's row, then its advisory lock, then the requests' rows; the approval takes the
// medication's locks before the request's row (RefillRequestQueries.LockMedicationSql), or the two could each wait for
// the other. Ehr.Security.Tests runs that SQL itself; this runs the app's code, so it fails if SignAsync stops taking the
// locks first. A third connection holds the medication's row until both are queued behind it, in one order or the
// other: marking queued first is the order that deadlocked.
//
// A request whose medication's entry was changed on the chart since it was asked (its drug code with it) is only
// declined: the review approved a request for amlodipine 5 mg with 10 mg, and the portal said so.
public class RefillApprovalTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approving_while_the_medication_is_marked_sensitive_never_deadlocks(bool markedFirst)
    {
        var (clinician, _, patient) = await PortalPatientAsync(apps, markedFirst ? "Marked" : "Approved");
        var medication = await MedicationAsync(apps, patient.Patient);
        using (var asked = await AskAsync(patient.Client, Lisinopril))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);

        // The approval, as the order form runs it: the clinician's own scope and transaction.
        await using var scope = apps.OnPrem.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().UserId = clinician.Id;
        var db = scope.ServiceProvider.GetRequiredService<EhrDbContext>();
        var orders = scope.ServiceProvider.GetRequiredService<ClinicalOrders>();
        var item = await db.OrderCatalogItems.AsNoTracking().SingleAsync(i => i.Code == LisinoprilCode, Cancel);
        await using var approving = await db.Database.BeginTransactionAsync(Cancel);
        var approver = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;

        // The colleague marking the medication sensitive, and the connection holding its row.
        await using var marking = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await marking.OpenAsync(Cancel);
        await using var markingTransaction = await marking.BeginTransactionAsync(Cancel);
        await using (var user = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", marking, markingTransaction))
        {
            user.Parameters.AddWithValue("user", clinician.Id.ToString());
            await user.ExecuteNonQueryAsync(Cancel);
        }

        await using var holding = await apps.OpenDatabaseAsync();
        await using var holdingTransaction = await holding.BeginTransactionAsync(Cancel);
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ehr.medication_statements WHERE id = @id FOR UPDATE", holding, holdingTransaction))
        {
            hold.Parameters.AddWithValue("id", medication);
            await hold.ExecuteNonQueryAsync(Cancel);
        }

        async Task MarkAsync()
        {
            await using var mark = new NpgsqlCommand("UPDATE ehr.medication_statements SET sensitive = true WHERE id = @id", marking, markingTransaction);
            mark.Parameters.AddWithValue("id", medication);
            await mark.ExecuteNonQueryAsync(Cancel);
        }

        Task<(Ehr.Data.Orders.Order? Order, SignRefusal? Refusal)> ApproveAsync() => orders.SignAsync(patient.Patient, item, "{}", null, Cancel, refill: request);

        Task marked;
        Task<(Ehr.Data.Orders.Order? Order, SignRefusal? Refusal)> approved;
        if (markedFirst)
        {
            marked = MarkAsync();
            await WaitingAsync(marking.ProcessID);
            approved = ApproveAsync();
            await WaitingAsync(approver);
        }
        else
        {
            approved = ApproveAsync();
            await WaitingAsync(approver);
            marked = MarkAsync();
            await WaitingAsync(marking.ProcessID);
        }

        await holdingTransaction.RollbackAsync(Cancel);
        if (markedFirst)
        {
            await marked;
            await markingTransaction.CommitAsync(Cancel);
            Assert.Null((await approved).Refusal);
            await approving.CommitAsync(Cancel);
        }
        else
        {
            Assert.Null((await approved).Refusal);
            await approving.CommitAsync(Cancel);
            await marked;
            await markingTransaction.CommitAsync(Cancel);
        }

        Assert.Equal(["completed true"], await ProxyTests.QueryAsync(apps,
            $"SELECT status || ' ' || sensitive FROM ehr.refill_requests WHERE id = '{request}' AND patient_id = @patient", patient.Patient));
    }

    [Fact]
    public async Task A_request_whose_medication_was_changed_since_it_was_asked_is_only_declined()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Changed");
        var amlodipine = await MedicationAsync(apps, patient.Patient, "amlodipine 5 MG Oral Tablet", "197361");
        using (var asked = await AskAsync(patient.Client, "amlodipine 5 MG Oral Tablet"))
        {
            Assert.Equal(PortalPath, PathOf(asked));
        }

        var request = await RequestIdAsync(apps, patient.Patient);
        var formBefore = await session.Client.GetStringAsync(ApprovePath(patient.Patient, request), Cancel);

        // The clinician changes the chart's entry to the 10 mg tablet, from the medication list's form.
        using (var changed = await ChartListTests.SaveAsync(session.Client, patient.Patient, "medications", amlodipine,
            ("Name", "amlodipine 10 MG Oral Tablet"), ("Status", "active")))
        {
            Assert.True(changed.IsSuccessStatusCode);
        }

        Assert.Equal(["308135"], await ProxyTests.QueryAsync(apps, $"SELECT code FROM ehr.medication_statements WHERE id = '{amlodipine}' AND patient_id = @patient",
            patient.Patient));

        var page = await session.Client.GetStringAsync(ReviewPath(patient.Patient, request), Cancel);
        Assert.Contains("Can't be approved: the medication on the chart has changed since this was asked.", Said(page));
        Assert.DoesNotContain("data-refill-approve", page);
        using (var form = await session.Client.GetAsync(ApprovePath(patient.Patient, request), Cancel))
        {
            Assert.Equal(ReviewPath(patient.Patient, request), PathOf(form));
        }

        // The form opened before the change, posted after it with the new product: nothing is ordered.
        using (var refused = await session.Client.PostAsync(ApprovePath(patient.Patient, request), Form(("__RequestVerificationToken", Token(formBefore)),
            ("Item", "amlodipine 10 MG Oral Tablet"), ("SafetySeen", "true"), ("Details[dose]", "10 mg"), ("Details[route]", "By mouth"),
            ("Details[frequency]", "Once a day")), Cancel))
        {
            Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("This refill request can't be approved.", Said(await refused.Content.ReadAsStringAsync(Cancel)));
        }

        Assert.Equal(["0"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.orders WHERE patient_id = @patient", patient.Patient));
        using (var declined = await DeclineAsync(session.Client, patient.Patient, request, "Synthetic: your dose changed; please book a visit."))
        {
            Assert.Contains("Declined:", Said(await ReadAsync(declined)));
        }

        Assert.StartsWith("rejected|amlodipine 5 MG Oral Tablet|", (await RequestsAsync(apps, patient.Patient))[0], StringComparison.Ordinal);
    }

    // Until the backend is waiting for a lock.
    async Task WaitingAsync(int process)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT coalesce(wait_event_type = 'Lock', false) FROM pg_stat_activity WHERE pid = @pid", db);
        command.Parameters.AddWithValue("pid", process);
        for (var tries = 0; tries < 400; tries++)
        {
            if (await command.ExecuteScalarAsync(Cancel) is true)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), Cancel);
        }

        Assert.Fail("The connection never waited for a lock.");
    }
}
