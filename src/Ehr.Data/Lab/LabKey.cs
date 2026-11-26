using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Lab;

// The key a lab's system calls the on-prem app with (the expansion plan's step 12, UC 20), as its SHA-256: the key
// itself is never stored. The migration job writes it from EHR_LAB_KEY on every run (ehr.set_lab_key), and only the
// functions of the lab's door read it (db/policies/76-lab.sql). Internal, with no grant to the runtime roles, so SQL as
// the apps' roles without the key's hash gets nothing through that door. No row: no lab. The apps never read the table,
// so the context has no set for it.
public class LabKey
{
    // Hex SHA-256 of the key.
    public required string KeyHash { get; set; }
}

// Maps ehr.lab_keys. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class LabKeyConfiguration : IEntityTypeConfiguration<LabKey>
{
    public void Configure(EntityTypeBuilder<LabKey> key)
    {
        key.ToTable("lab_keys", table => table.HasCheckConstraint("ck_lab_keys_key_hash", "key_hash ~ '^[0-9a-f]{64}$'"));
        // The door finds the lab by the hash it is given, through this key.
        key.HasKey(k => k.KeyHash);
        key.Property(k => k.KeyHash).HasMaxLength(64);
    }
}
