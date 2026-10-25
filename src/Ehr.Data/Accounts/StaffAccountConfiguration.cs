using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Accounts;

// Maps the staff account tables the way ASP.NET Core Identity's own IdentityUserContext would, under this schema's
// names: the keys, the unique user name, the email index, the lengths, the concurrency stamp, and the three
// dependent tables that hang off an account. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class StaffAccountConfiguration :
    IEntityTypeConfiguration<StaffUser>,
    IEntityTypeConfiguration<StaffUserClaim>,
    IEntityTypeConfiguration<StaffUserLogin>,
    IEntityTypeConfiguration<StaffUserToken>
{
    public void Configure(EntityTypeBuilder<StaffUser> user)
    {
        // Only the roles in StaffRoles, enforced where the data lives.
        var allowed = string.Join(", ", StaffRoles.All.Select(role => $"'{role}'"));
        user.ToTable("staff_users", table => table.HasCheckConstraint("ck_staff_users_roles", $"roles <@ ARRAY[{allowed}]::text[]"));

        user.HasKey(u => u.Id);
        user.HasIndex(u => u.NormalizedUserName).IsUnique();
        user.HasIndex(u => u.NormalizedEmail);
        user.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
        user.Property(u => u.UserName).HasMaxLength(256);
        user.Property(u => u.NormalizedUserName).HasMaxLength(256);
        user.Property(u => u.Email).HasMaxLength(256);
        user.Property(u => u.NormalizedEmail).HasMaxLength(256);
        user.Property(u => u.DisplayName).HasMaxLength(200);

        user.HasMany<StaffUserClaim>().WithOne().HasForeignKey(claim => claim.UserId).IsRequired();
        user.HasMany<StaffUserLogin>().WithOne().HasForeignKey(login => login.UserId).IsRequired();
        user.HasMany<StaffUserToken>().WithOne().HasForeignKey(token => token.UserId).IsRequired();
    }

    public void Configure(EntityTypeBuilder<StaffUserClaim> claim)
    {
        claim.ToTable("staff_user_claims");
        claim.HasKey(c => c.Id);
    }

    public void Configure(EntityTypeBuilder<StaffUserLogin> login)
    {
        login.ToTable("staff_user_logins");
        login.HasKey(l => new { l.LoginProvider, l.ProviderKey });
    }

    public void Configure(EntityTypeBuilder<StaffUserToken> token)
    {
        token.ToTable("staff_user_tokens");
        token.HasKey(t => new { t.UserId, t.LoginProvider, t.Name });
    }
}
