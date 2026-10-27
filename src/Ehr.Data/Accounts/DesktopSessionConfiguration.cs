using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Accounts;

// Maps ehr.desktop_sessions. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class DesktopSessionConfiguration : IEntityTypeConfiguration<DesktopSession>
{
    public void Configure(EntityTypeBuilder<DesktopSession> session)
    {
        session.ToTable("desktop_sessions");
        session.Property(s => s.TokenHash).HasMaxLength(64);
        // Every request finds the session by its hash; two tokens never share one.
        session.HasIndex(s => s.TokenHash).IsUnique();
        session.Property(s => s.SecurityStamp).HasMaxLength(64);
        session.Property(s => s.Device).HasMaxLength(100);
        session.HasIndex(s => s.StaffUserId);
        session.HasOne<StaffUser>().WithMany().HasForeignKey(s => s.StaffUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
