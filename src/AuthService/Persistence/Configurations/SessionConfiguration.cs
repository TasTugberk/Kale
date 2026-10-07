using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class SessionConfiguration : BaseEntityConfiguration<Session>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_sessions_expires_after_created", "expires_at > created_at"));

        // The chosen role must be a role of the session's application.
        // Cascade: deleting a role ends its sessions; a missing session reads as "not found", so clients deny.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(s => new { s.ApplicationId, s.RoleId })
            .HasPrincipalKey(r => new { r.ApplicationId, r.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sessions_role_same_application");

        // A session needs an access row for this user and application (DESIGN.md: sign-in requires an active
        // UserApplication). Cascade: removing access, or the user, removes their sessions for that application.
        builder.HasOne<UserApplication>()
            .WithMany()
            .HasForeignKey(s => new { s.UserId, s.ApplicationId })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_sessions_user_application");

        // "Stop all sessions of this user" only looks at sessions that haven't ended.
        builder.HasIndex(s => s.UserId).HasFilter("ended_at IS NULL");
    }
}
