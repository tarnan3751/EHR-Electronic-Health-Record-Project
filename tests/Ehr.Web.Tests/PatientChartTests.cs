using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The chart (Areas/Clinical/Pages/Patients/Chart, PhiReader.ChartAsync): demographics, the allergies, medications and
// diagnoses the patient has now, and recent visits with the latest one's vital signs, each section saying so when
// nothing is recorded. Opening it is one recorded read of the patient, on-site and off-site.
public class PatientChartTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Today = DateTimeOffset.UtcNow;

    [Fact]
    public async Task The_chart_shows_active_allergies_medications_and_diagnoses_and_the_latest_visits_vitals()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Wren", "Charted");
        await apps.AssignAsync(clinician, patient);
        await apps.InsertAllergyAsync(patient, "Penicillin", criticality: AllergyCriticality.High, reaction: "Hives");
        await apps.InsertMedicationAsync(patient, "Lisinopril 10 mg tablet", dosage: "1 tablet by mouth daily");
        await apps.InsertConditionAsync(patient, "Essential (primary) hypertension", code: "I10", onset: new DateOnly(2019, 4, 1));
        await apps.InsertVisitAsync(patient, Today.AddDays(-40), reason: "Blood pressure check", systolic: 141, diastolic: 90, heartRate: 80);
        await apps.InsertVisitAsync(patient, Today.AddDays(-3), reason: "Annual physical", systolic: 128, diastolic: 82, heartRate: 72);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        Assert.Contains("Charted, Wren", html);
        Assert.Contains("<strong>Penicillin</strong>: Hives", html);
        Assert.Contains("High risk", html);
        Assert.Contains("<strong>Lisinopril 10 mg tablet</strong>: 1 tablet by mouth daily", html);
        Assert.Contains("Essential (primary) hypertension", html);
        Assert.Contains("(I10)", html);
        Assert.Contains("since 2019-04-01", html);
        Assert.Contains($"Latest visit, {Today.AddDays(-3):yyyy-MM-dd}:", html);
        Assert.Contains("blood pressure 128/82 mmHg, heart rate 72/min.", html);
        Assert.Contains("Annual physical", html);
        Assert.Contains("Blood pressure check", html);
        Assert.Contains("141/90 mmHg", html);
    }

    [Fact]
    public async Task Stopped_medications_resolved_diagnoses_and_visits_that_never_happened_are_left_out()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Ash", "Pruned");
        await apps.AssignAsync(clinician, patient);
        await apps.InsertAllergyAsync(patient, "Latex", status: AllergyClinicalStatus.Resolved);
        await apps.InsertAllergyAsync(patient, "Peanut");
        await apps.InsertMedicationAsync(patient, "Amoxicillin 500 mg capsule", status: MedicationStatementStatus.Stopped);
        await apps.InsertMedicationAsync(patient, "Metformin 500 mg tablet");
        await apps.InsertConditionAsync(patient, "Acute bronchitis", status: ConditionClinicalStatus.Resolved);
        await apps.InsertConditionAsync(patient, "Type 2 diabetes mellitus", status: ConditionClinicalStatus.Recurrence);
        await apps.InsertVisitAsync(patient, Today.AddDays(-10), reason: "Follow-up");
        await apps.InsertVisitAsync(patient, Today.AddDays(-2), status: EncounterStatus.Cancelled, reason: "Cancelled appointment");
        await apps.InsertVisitAsync(patient, Today.AddDays(7), status: EncounterStatus.Planned, reason: "Planned appointment");
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        Assert.Contains("Peanut", html);
        Assert.DoesNotContain("Latex", html);
        Assert.Contains("Metformin 500 mg tablet", html);
        Assert.DoesNotContain("Amoxicillin", html);
        Assert.Contains("Type 2 diabetes mellitus", html);
        Assert.DoesNotContain("Acute bronchitis", html);
        Assert.Contains("Follow-up", html);
        Assert.DoesNotContain("Cancelled appointment", html);
        Assert.DoesNotContain("Planned appointment", html);
        // The latest visit that happened, without vital signs.
        Assert.Contains($"Latest visit, {Today.AddDays(-10):yyyy-MM-dd}:", html);
        Assert.Contains("blood pressure not taken, heart rate not taken.", html);
    }

    [Fact]
    public async Task Only_the_five_latest_visits_are_shown_newest_first()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Cedar", "Frequent");
        await apps.AssignAsync(clinician, patient);
        for (var weeks = 1; weeks <= 7; weeks++)
        {
            await apps.InsertVisitAsync(patient, Today.AddDays(-7 * weeks), reason: $"Weekly check {weeks}");
        }

        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        var shown = Enumerable.Range(1, 5).Select(weeks => html.IndexOf($"Weekly check {weeks}<", StringComparison.Ordinal)).ToList();
        Assert.All(shown, position => Assert.True(position >= 0));
        Assert.Equal(shown.Order(), shown);
        Assert.DoesNotContain("Weekly check 6", html);
        Assert.DoesNotContain("Weekly check 7", html);
    }

    [Fact]
    public async Task A_chart_with_nothing_recorded_says_so_in_every_section()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Birch", "Blank");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        Assert.Contains("No allergy information recorded.", html);
        Assert.Contains("No active medications recorded.", html);
        Assert.Contains("No active diagnoses recorded.", html);
        Assert.Contains("No visits recorded.", html);
        Assert.DoesNotContain("Latest visit", html);
    }

    [Fact]
    public async Task No_known_allergies_reads_differently_from_no_allergy_information()
    {
        var clinician = await apps.ClinicianAsync();
        var none = await apps.InsertPatientAsync("Alder", "Nka");
        var unknown = await apps.InsertPatientAsync("Alder", "Unasked");
        var allergic = await apps.InsertPatientAsync("Alder", "Contradicted");
        foreach (var patient in new[] { none, unknown, allergic })
        {
            await apps.AssignAsync(clinician, patient);
        }

        await apps.InsertAllergyAsync(none, "No known allergy", code: AllergyIntolerance.NoKnownAllergy);
        // Recorded as having none, then an allergy found: the allergy is what counts.
        await apps.InsertAllergyAsync(allergic, "No known allergy", code: AllergyIntolerance.NoKnownAllergy);
        await apps.InsertAllergyAsync(allergic, "Sulfonamides");
        var session = await SignInAsync(apps.OnPrem, clinician);

        var noneHtml = await session.Client.GetStringAsync($"/patients/{none}", Cancel);
        var unknownHtml = await session.Client.GetStringAsync($"/patients/{unknown}", Cancel);
        var allergicHtml = await session.Client.GetStringAsync($"/patients/{allergic}", Cancel);

        Assert.Contains("No known allergies (recorded 2025-03-04).", noneHtml);
        Assert.DoesNotContain("No allergy information recorded.", noneHtml);
        Assert.Contains("No allergy information recorded.", unknownHtml);
        Assert.DoesNotContain("No known allergies", unknownHtml);
        Assert.Contains("Sulfonamides", allergicHtml);
        Assert.DoesNotContain("No known allerg", allergicHtml);
    }

    [Fact]
    public async Task Opening_a_chart_records_one_read_of_the_patient_and_nothing_else()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Counted", Ehr.Data.Accounts.StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Elm", "Counted");
        await apps.AssignAsync(clinician, patient);
        await apps.InsertAllergyAsync(patient, "Penicillin");
        await apps.InsertMedicationAsync(patient, "Lisinopril 10 mg tablet");
        await apps.InsertConditionAsync(patient, "Essential (primary) hypertension");
        await apps.InsertVisitAsync(patient, Today.AddDays(-1), systolic: 120, diastolic: 80, heartRate: 70);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var before = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);

        using var response = await session.Client.GetAsync($"/patients/{patient}", Cancel);
        await ReadAsync(response);

        Assert.Equal(before + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
        Assert.Equal(0, await ReadsOfOtherTablesAsync(patient));
    }

    [Fact]
    public async Task Without_a_search_the_chart_links_back_to_the_panel()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Fir", "Returning");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

        Assert.Contains("<a href=\"/patients\">Back to my patients</a>", html);
    }

    // Off-site with the on-prem app unable to save, the chart comes from the replica with the read-only notice, and its
    // one read waits in the cloud app's queue until the on-prem app takes it.
    [Fact]
    public async Task Off_site_during_an_outage_the_chart_comes_from_the_replica_and_its_one_read_is_queued_then_shipped()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Remote", Ehr.Data.Accounts.StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Hazel", "Outage");
        await apps.InsertAllergyAsync(patient, "Penicillin");
        await apps.InsertVisitAsync(patient, Today.AddDays(-1), systolic: 118, diastolic: 76, heartRate: 64);
        // Signed in first: the home lists the panel, and that would be a read too.
        var session = await SignInAsync(apps.CloudPrimaryDrill, clinician, via: apps.Cloud);
        await apps.AssignAsync(clinician, patient);
        var shipper = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>();
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await shipper.ShipAsync(Cancel));

            var html = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);

            Assert.Contains("Outage, Hazel", html);
            Assert.Contains("Penicillin", html);
            Assert.Contains("blood pressure 118/76 mmHg, heart rate 64/min.", html);
            Assert.Contains("Read-only: the clinic's server can't be reached", html);
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            var read = Assert.Single(queued, e => e.PatientId == patient);
            Assert.Equal(AuditActions.Read, read.Action);
            Assert.Equal("patients", read.TableName);
            Assert.Equal(patient, read.RowId);
            Assert.Equal(clinician.Id, read.ActorId);
            Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));

            apps.PrimaryDown = false;
            Assert.True(await shipper.ShipAsync(Cancel));
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    // Reads recorded against the patient for anything but their patients row: the chart's sections are part of the one
    // read of the patient.
    async Task<long> ReadsOfOtherTablesAsync(Guid patient)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM ehr.audit_events WHERE action = 'read' AND patient_id = @patient AND table_name <> 'patients'", connection);
        command.Parameters.AddWithValue("patient", patient);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
