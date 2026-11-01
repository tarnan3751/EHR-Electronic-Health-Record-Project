using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Accounts;

// Maps the patient account tables the way ASP.NET Core Identity's own IdentityUserContext would, under this
// schema's names: the keys, the unique user name, the email index, the lengths, the concurrency stamp, and the
// three dependent tables that hang off an account. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class PatientAccountConfiguration :
    IEntityTypeConfiguration<PatientUser>,
    IEntityTypeConfiguration<PatientUserClaim>,
    IEntityTypeConfiguration<PatientUserLogin>,
    IEntityTypeConfiguration<PatientUserToken>
{
    public void Configure(EntityTypeBuilder<PatientUser> user)
    {
        user.ToTable("patient_users");
        user.HasKey(u => u.Id);
        user.HasIndex(u => u.NormalizedUserName).IsUnique();
        user.HasIndex(u => u.NormalizedEmail);
        user.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
        user.Property(u => u.UserName).HasMaxLength(256);
        user.Property(u => u.NormalizedUserName).HasMaxLength(256);
        user.Property(u => u.Email).HasMaxLength(256);
        user.Property(u => u.NormalizedEmail).HasMaxLength(256);

        // One account per patient, and never an account without its patient. Nothing deletes patients, but if
        // something ever did, the account would stand in the way rather than vanish.
        user.HasIndex(u => u.PatientId).IsUnique();
        user.HasOne<Patient>().WithMany().HasForeignKey(u => u.PatientId).OnDelete(DeleteBehavior.Restrict);

        user.HasMany<PatientUserClaim>().WithOne().HasForeignKey(claim => claim.UserId).IsRequired();
        user.HasMany<PatientUserLogin>().WithOne().HasForeignKey(login => login.UserId).IsRequired();
        user.HasMany<PatientUserToken>().WithOne().HasForeignKey(token => token.UserId).IsRequired();
    }

    public void Configure(EntityTypeBuilder<PatientUserClaim> claim)
    {
        claim.ToTable("patient_user_claims");
        claim.HasKey(c => c.Id);
    }

    public void Configure(EntityTypeBuilder<PatientUserLogin> login)
    {
        login.ToTable("patient_user_logins");
        login.HasKey(l => new { l.LoginProvider, l.ProviderKey });
    }

    public void Configure(EntityTypeBuilder<PatientUserToken> token)
    {
        token.ToTable("patient_user_tokens");
        token.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
    }
}
