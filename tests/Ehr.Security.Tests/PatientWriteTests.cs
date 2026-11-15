using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ehr.Security.Tests;

// ehr.patients keeps generated name keys for the search (PatientConfiguration). EF Core reads generated columns back
// with RETURNING after an insert or update, and PostgreSQL applies the table's SELECT policy to the rows it returns.
// A clinician, the only account the policies let write patients, passes it: a write through EF Core, as the app would
// make one, works and reads the keys back, lower-cased by the database.
public class PatientWriteTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    [Fact]
    public async Task A_clinician_saves_a_patient_through_EF_Core_and_reads_back_its_search_keys()
    {
        var cancel = TestContext.Current.CancellationToken;
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        options.AddInterceptors(new UserContextInterceptor(new UserContext { UserId = clinician }));
        await using var db = new EhrDbContext(options.Options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        var id = Guid.CreateVersion7();
        var patient = new Patient
        {
            Id = id,
            Mrn = id.ToString("N")[^12..],
            GivenName = "Ömer",
            FamilyName = "ÅSTRÖM",
            BirthDate = new DateOnly(1980, 1, 1),
            Gender = AdministrativeGender.Unknown,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        db.Patients.Add(patient);
        await db.SaveChangesAsync(cancel);
        var inserted = (patient.GivenNameKey, patient.FamilyNameKey);
        patient.FamilyName = "Lindqvist";
        patient.Version = 2;
        await db.SaveChangesAsync(cancel);
        await transaction.CommitAsync(cancel);

        Assert.Equal(("ömer", "åström"), inserted);
        Assert.Equal("lindqvist", patient.FamilyNameKey);
    }
}
