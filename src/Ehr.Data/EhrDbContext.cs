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

        modelBuilder.Entity<QuickText>(quickText =>
        {
            quickText.Property(q => q.Shortcut).HasMaxLength(32);
            quickText.HasIndex(q => q.Shortcut).IsUnique();
            quickText.Property(q => q.Body).HasMaxLength(4000);
            quickText.Property(q => q.Version).IsConcurrencyToken();
        });
    }
}
