using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests;

// The FHIR API's reads (the expansion plan's step 11) below what the test cases check: what one read by ID costs the
// database, and the order each kind of entry comes in, which the capability statement states.
public class FhirReaderTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // The blood pressure codes the API leaves out (FhirObservations.LeftOut).
    static readonly string[] LeftOut = [VitalSign.Systolic, VitalSign.Diastolic];

    [Fact]
    public async Task A_read_by_id_is_one_query_and_never_counts_and_a_search_cut_by_its_limit_does()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Counted");
        var user = new UserContext { UserId = chart.Clinician.Id };
        var commands = new PatientSearchQueryTests.CapturedCommands();
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(user), commands);
        await using var db = new EhrDbContext(options.Options);
        var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
        List<string> Sent(string table) =>
            [.. commands.Executed.Select(command => command.Text).Where(text => text.Contains($"FROM ehr.{table}", StringComparison.Ordinal))];
        bool Counted() => commands.Executed.Any(command => command.Text.Contains("count(*)", StringComparison.OrdinalIgnoreCase));

        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        commands.Executed.Clear();
        var observation = await reader.ObservationsAsync(chart.Patient, chart.Vitals[VitalSign.HeartRate], category: null, LeftOut, limit: 1, Cancel);
        Assert.Equal((1, 1), (observation.Entries.Count, observation.Total));
        Assert.Single(Sent("observations"));
        Assert.False(Counted());

        commands.Executed.Clear();
        var appointment = await reader.AllAppointmentsAsync(chart.Patient, chart.Booked, limit: 1, Cancel);
        Assert.Equal((1, 1), (appointment.Entries.Count, appointment.Total));
        Assert.Single(Sent("appointments"));
        Assert.False(Counted());

        // The capture sees a count when there is one: a search the limit cut asks how many there are in all.
        commands.Executed.Clear();
        var cut = await reader.ObservationsAsync(chart.Patient, only: null, category: null, LeftOut, limit: 1, Cancel);
        Assert.Single(cut.Entries);
        Assert.True(cut.Total > 1);
        Assert.True(Counted());
        await transaction.RollbackAsync(Cancel);
    }

    [Fact]
    public async Task Each_kind_of_entry_comes_newest_first_by_its_own_date_and_undated_ones_last()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Fhir Ordered", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Fhirordered");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        (string Display, object Date)[] medications =
            [("Synthetic: started 2023", new DateOnly(2023, 2, 1)), ("Synthetic: no start", DBNull.Value), ("Synthetic: started 2025", new DateOnly(2025, 6, 1))];
        foreach (var (display, started) in medications)
        {
            await InsertAsync(apps, "INSERT INTO ehr.medication_statements (id, patient_id, display, status, effective_start) VALUES (@id, @patient, @display, 'active', @started)",
                patient, ("display", display), ("started", started));
        }

        (string Display, object Date)[] conditions =
            [("Synthetic: onset 2020", new DateOnly(2020, 3, 1)), ("Synthetic: no onset", DBNull.Value), ("Synthetic: onset 2024", new DateOnly(2024, 9, 1))];
        foreach (var (display, onset) in conditions)
        {
            await InsertAsync(apps, "INSERT INTO ehr.conditions (id, patient_id, display, clinical_status, onset_date) VALUES (@id, @patient, @display, 'active', @onset)",
                patient, ("display", display), ("onset", onset));
        }

        foreach (var (display, recorded) in new[] { ("Synthetic: recorded 2021", new DateTimeOffset(2021, 1, 5, 9, 0, 0, TimeSpan.Zero)),
            ("Synthetic: recorded 2024", new DateTimeOffset(2024, 1, 5, 9, 0, 0, TimeSpan.Zero)) })
        {
            await InsertAsync(apps, "INSERT INTO ehr.allergy_intolerances (id, patient_id, display, clinical_status, recorded_at) VALUES (@id, @patient, @display, 'active', @recorded)",
                patient, ("display", display), ("recorded", recorded));
        }

        var (_, medicationsFound, _) = Searched<R4.MedicationStatement>(await GetAsync(session.Client, $"/fhir/MedicationStatement?patient={patient}"));
        Assert.Equal(["Synthetic: started 2025", "Synthetic: started 2023", "Synthetic: no start"],
            medicationsFound.Select(m => Assert.IsType<R4.CodeableConcept>(m.Medication).Text));
        var (_, conditionsFound, _) = Searched<R4.Condition>(await GetAsync(session.Client, $"/fhir/Condition?patient={patient}"));
        Assert.Equal(["Synthetic: onset 2024", "Synthetic: onset 2020", "Synthetic: no onset"], conditionsFound.Select(c => c.Code!.Text));
        var (_, allergies, _) = Searched<R4.AllergyIntolerance>(await GetAsync(session.Client, $"/fhir/AllergyIntolerance?patient={patient}"));
        Assert.Equal(["Synthetic: recorded 2024", "Synthetic: recorded 2021"], allergies.Select(a => a.Code!.Text));
    }
}
