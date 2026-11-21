using System.Net;
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
// database, the order each kind of entry comes in, which the capability statement states, and what a blood pressure
// code on a row that is no reading's value is.
public class FhirReaderTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
        var observation = await reader.ObservationsAsync(chart.Patient, chart.Vitals[VitalSign.HeartRate], category: null, limit: 1, Cancel);
        Assert.Equal((1, 1), (observation.Entries.Count, observation.Total));
        Assert.Single(Sent("observations"));
        Assert.False(Counted());

        // A blood pressure reading by its ID: its two values in one statement, paired by the reading, and no count.
        commands.Executed.Clear();
        var reading = await reader.ObservationsAsync(chart.Patient, chart.Reading, category: null, limit: 1, Cancel);
        var pressure = Assert.Single(reading.Entries);
        Assert.Equal(1, reading.Total);
        Assert.Equal((VitalSign.Systolic, chart.Reading, VitalSign.Diastolic, chart.Reading),
            (pressure.Observation.Code, pressure.Observation.ReadingId, pressure.Diastolic?.Observation.Code, pressure.Diastolic?.Observation.ReadingId));
        Assert.Single(Sent("observations"));
        Assert.False(Counted());

        // Neither value's own ID is an entry: not found, by the gate's question or by the read.
        foreach (var value in new[] { chart.Vitals[VitalSign.Systolic], chart.Vitals[VitalSign.Diastolic] })
        {
            Assert.Null(await reader.EntryPatientAsync("observations", value, sensitive: false, Cancel));
            Assert.Empty((await reader.ObservationsAsync(chart.Patient, value, category: null, limit: 1, Cancel)).Entries);
        }

        commands.Executed.Clear();
        var appointment = await reader.AllAppointmentsAsync(chart.Patient, chart.Booked, limit: 1, Cancel);
        Assert.Equal((1, 1), (appointment.Entries.Count, appointment.Total));
        Assert.Single(Sent("appointments"));
        Assert.False(Counted());

        // A search counts a reading once: the seven other vital signs and the one reading.
        var signs = await reader.ObservationsAsync(chart.Patient, only: null, ObservationCategory.VitalSigns, limit: 600, Cancel);
        Assert.Equal((8, 8), (signs.Entries.Count, signs.Total));
        Assert.Equal(chart.Reading, Assert.Single(signs.Entries, entry => entry.Diastolic is not null).Observation.ReadingId);

        // The capture sees a count when there is one: a search the limit cut asks how many there are in all, readings
        // once each.
        commands.Executed.Clear();
        var cut = await reader.ObservationsAsync(chart.Patient, only: null, ObservationCategory.VitalSigns, limit: 1, Cancel);
        Assert.Single(cut.Entries);
        Assert.Equal(8, cut.Total);
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

    // A blood pressure code makes no reading. A laboratory row can carry one with no reading ID, as a lab interface may
    // send it (ck_observations_reading ties the two codes to readings among vital signs only): it is an observation like
    // any other, an entry of its own, counted, read by its own ID and sent as itself, laboratory, claiming no profile.
    // Only a reading's diastolic is no entry, being part of its systolic's. The review's probe: two readings and a newer
    // laboratory diastolic, searched with limits of 1 and 2, came back with no entry and a total of 2; and a laboratory
    // systolic read by its ID was recorded as read and then not found.
    [Fact]
    public async Task A_blood_pressure_code_on_a_row_with_no_reading_is_an_observation_of_its_own()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Fhir Laboratory", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Fhirlaboratory");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        foreach (var (minutesAgo, systolic, diastolic) in new[] { (90, "131", "85"), (60, "122", "79") })
        {
            using var recorded = await VitalTests.RecordAsync(session.Client, patient, minutesAgo, (VitalSign.Systolic, systolic), (VitalSign.Diastolic, diastolic));
            Assert.Equal(VitalTests.Path(patient), PathOf(recorded));
        }

        // The newer reading first.
        var readings = (await ProxyTests.QueryAsync(apps,
            "SELECT reading_id::text FROM ehr.observations WHERE patient_id = @patient AND code = '8480-6' AND reading_id IS NOT NULL ORDER BY effective_at DESC", patient))
            .Select(Guid.Parse).ToList();
        var readingsDiastolic = Guid.Parse((await ProxyTests.QueryAsync(apps,
            "SELECT id::text FROM ehr.observations WHERE patient_id = @patient AND code = '8462-4' AND reading_id IS NOT NULL ORDER BY effective_at DESC LIMIT 1", patient)).Single());
        var now = DateTimeOffset.UtcNow;
        var newest = now.AddMinutes(-10);
        var oldest = now.AddHours(-3);
        var laboratoryDiastolic = await apps.InsertResultAsync(patient, "Diastolic blood pressure", VitalSign.Diastolic, 78m, "mm[Hg]", 60m, 80m, newest, newest);
        var laboratorySystolic = await apps.InsertResultAsync(patient, "Systolic blood pressure", VitalSign.Systolic, 119m, "mm[Hg]", 90m, 120m, oldest, oldest);
        Guid[] expected = [laboratoryDiastolic, readings[0], readings[1], laboratorySystolic];

        // In the reader, at each limit: each entry by the ID the API sends it under.
        var user = new UserContext { UserId = clinician.Id };
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(user));
        await using (var db = new EhrDbContext(options.Options))
        {
            var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            foreach (var limit in new[] { 1, 2, 3, 4, 600 })
            {
                var read = await reader.ObservationsAsync(patient, only: null, category: null, limit, Cancel);
                Assert.Equal(expected.Take(limit), read.Entries.Select(entry => entry.Observation.ReadingId ?? entry.Observation.Id));
                Assert.Equal(4, read.Total);
            }

            foreach (var laboratory in new[] { laboratoryDiastolic, laboratorySystolic })
            {
                Assert.Equal(patient, await reader.EntryPatientAsync("observations", laboratory, sensitive: false, Cancel));
                Assert.Equal(laboratory, Assert.Single((await reader.ObservationsAsync(patient, laboratory, category: null, 1, Cancel)).Entries).Observation.Id);
            }

            await transaction.RollbackAsync(Cancel);
        }

        // Through the API: the search sends all four, the laboratory rows as themselves; the laboratory systolic is read
        // by its own ID; a reading's diastolic by its own ID is still not found, and is not recorded as read.
        var before = await ReadsAsync(apps, clinician.Id, patient);
        var (bundle, found, _) = Searched<R4.Observation>(await GetAsync(session.Client, $"/fhir/Observation?patient={patient}"));
        Assert.Equal(4, bundle.Total);
        Assert.Equal(expected.Select(id => id.ToString()), found.Select(observation => observation.Id));
        var laboratorySystolicRead = FhirConformance.Valid<R4.Observation>(
            Expect(await GetAsync(session.Client, $"/fhir/Observation/{laboratorySystolic}"), HttpStatusCode.OK).Json);
        foreach (var laboratory in new[] { found[0], found[3], laboratorySystolicRead })
        {
            Assert.Equal("laboratory", laboratory.Category.Single().Coding.Single().Code);
            Assert.Empty(laboratory.Meta?.Profile ?? []);
            Assert.Empty(laboratory.Component);
            Assert.IsType<R4.Quantity>(laboratory.Value);
        }

        Assert.Equal((laboratorySystolic.ToString(), VitalSign.Systolic), (laboratorySystolicRead.Id, laboratorySystolicRead.Code.Coding.Single().Code));
        Refused(await GetAsync(session.Client, $"/fhir/Observation/{readingsDiastolic}"), HttpStatusCode.NotFound, "not-found");
        Assert.Equal((before.Reads + 2, before.Sensitive), await ReadsAsync(apps, clinician.Id, patient));
    }
}
