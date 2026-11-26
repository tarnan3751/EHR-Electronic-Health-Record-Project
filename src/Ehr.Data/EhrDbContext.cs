using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Data;

// Tables live in the "ehr" schema, which ehr_owner owns. The runtime roles get table privileges only
// through db/policies/10-grants.sql.
public class EhrDbContext(DbContextOptions<EhrDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "ehr";

    public DbSet<QuickText> QuickTexts => Set<QuickText>();

    // ASP.NET Core Data Protection keys (antiforgery tokens, cookies). Created only on the primary;
    // the cloud app reads the same keys from the replica.
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // Sync: which rows each transaction changed (written by a trigger), and the push operations already applied.
    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();

    public DbSet<SyncOperation> SyncOperations => Set<SyncOperation>();

    // Accounts: staff and patients sign in separately, from separate tables (Accounts/). Not PHI, and not under
    // row-level security: sign-in finds the account before anyone is signed in.
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();

    public DbSet<PatientUser> PatientUsers => Set<PatientUser>();

    // The desktop app's sessions, by token hash. Grants only, like the account tables.
    public DbSet<DesktopSession> DesktopSessions => Set<DesktopSession>();

    // PHI: row-level security enabled and forced, every write recorded by trigger (db/policies). Read it only
    // through the PHI reader in Ehr.Domain.
    public DbSet<Patient> Patients => Set<Patient>();

    // Which patients are on which clinician's panel. PHI, like patients: read it through the PHI reader too.
    public DbSet<PatientAssignment> PatientAssignments => Set<PatientAssignment>();

    // Emergency access to a chart off the clinician's panel, with its reason. PHI; written once, never changed.
    public DbSet<BreakGlassAccess> BreakGlassAccesses => Set<BreakGlassAccess>();

    // The rest of a patient's chart, shaped on FHIR: allergies, the medications they take, diagnoses, and visits with
    // their vital signs. PHI, like patients: read them only through the PHI reader.
    public DbSet<AllergyIntolerance> AllergyIntolerances => Set<AllergyIntolerance>();

    public DbSet<MedicationStatement> MedicationStatements => Set<MedicationStatement>();

    public DbSet<Condition> Conditions => Set<Condition>();

    // Reference codes for chart entries (not PHI), and the history of changes to them (PHI).
    public DbSet<ClinicalCode> ClinicalCodes => Set<ClinicalCode>();

    public DbSet<ChartHistoryEntry> ChartHistory => Set<ChartHistoryEntry>();

    // Secure messages between a patient and their care team (FR-18; PHI).
    public DbSet<Messaging.MessageThread> MessageThreads => Set<Messaging.MessageThread>();

    public DbSet<Messaging.Message> Messages => Set<Messaging.Message>();

    public DbSet<Proxies.ProxyGrant> ProxyGrants => Set<Proxies.ProxyGrant>();

    // Appointments (FR-17; PHI).
    public DbSet<Scheduling.Appointment> Appointments => Set<Scheduling.Appointment>();

    // What needs a staff member's action (PHI: an item names a patient).
    public DbSet<Inbox.InboxItem> InboxItems => Set<Inbox.InboxItem>();

    // The rest of the reference data (Ehr.Data.Reference): not PHI.
    public DbSet<Reference.ObservationCode> ObservationCodes => Set<Reference.ObservationCode>();

    public DbSet<Reference.OrderResultCode> OrderResultCodes => Set<Reference.OrderResultCode>();

    public DbSet<Reference.Questionnaire> Questionnaires => Set<Reference.Questionnaire>();

    public DbSet<Reference.DrugIngredient> DrugIngredients => Set<Reference.DrugIngredient>();

    public DbSet<Reference.DrugClass> DrugClasses => Set<Reference.DrugClass>();

    public DbSet<Reference.DrugClassCrossReaction> DrugClassCrossReactions => Set<Reference.DrugClassCrossReaction>();

    public DbSet<Reference.DrugInteraction> DrugInteractions => Set<Reference.DrugInteraction>();

    public DbSet<Encounter> Encounters => Set<Encounter>();

    public DbSet<ClinicalDraft> ClinicalDrafts => Set<ClinicalDraft>();

    public DbSet<AiNoteReview> AiNoteReviews => Set<AiNoteReview>();
    public DbSet<AiFieldProposal> AiFieldProposals => Set<AiFieldProposal>();

    public DbSet<ClinicalNote> ClinicalNotes => Set<ClinicalNote>();

    // UC 1: the orders clinicians sign, PHI like the chart (read them only through the PHI reader), and the catalog of
    // what can be ordered, which isn't.
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderCatalogItem> OrderCatalogItems => Set<OrderCatalogItem>();

    // Test results, shaped on FHIR's Observation. PHI; a patient sees their own once released (the portal's results).
    public DbSet<Observation> Observations => Set<Observation>();

    // One-time portal enrollment codes, as hashes. Grants only: registration looks a code up before anyone is signed in.
    public DbSet<PatientEnrollment> PatientEnrollments => Set<PatientEnrollment>();

    // Every view and change of patient information, and every sign-in. Append-only.
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // Every host and tool configures the context through here, so they agree on naming and on where
    // migrations live: the Ehr.Migrations project in db/migrations.
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsAssembly("Ehr.Migrations")
                .MigrationsHistoryTable("__ef_migrations_history", Schema))
            .UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EhrDbContext).Assembly);

        modelBuilder.Entity<QuickText>(quickText =>
        {
            quickText.Property(q => q.Shortcut).HasMaxLength(32);
            quickText.HasIndex(q => q.Shortcut).IsUnique();
            quickText.Property(q => q.Body).HasMaxLength(4000);
            quickText.Property(q => q.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ChangeLogEntry>(entry =>
        {
            entry.Property(e => e.TransactionId).HasColumnType("xid8").HasDefaultValueSql("pg_current_xact_id()");
            entry.HasIndex(e => e.TransactionId);
            entry.Property(e => e.TableName).HasMaxLength(63);
        });

        modelBuilder.Entity<ClinicalDraft>(draft =>
        {
            draft.HasOne<Patient>().WithMany().HasForeignKey(d => d.PatientId);
            draft.HasOne<StaffUser>().WithMany().HasForeignKey(d => d.ClinicianId);
            draft.Property(d => d.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<SyncOperation>().HasKey(o => o.IdempotencyKey);

        // The name search's functions on what was typed (NameKeys).
        modelBuilder.HasDbFunction(typeof(NameKeys).GetMethod(nameof(NameKeys.Lower))!).HasName("lower").IsBuiltIn();
        modelBuilder.HasDbFunction(typeof(NameKeys).GetMethod(nameof(NameKeys.After))!).HasName("name_key_after").HasSchema(Schema);
    }
}
