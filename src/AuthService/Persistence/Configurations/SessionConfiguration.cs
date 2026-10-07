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
        // Restrict, not cascade: a cascade would remove sessions inside the database, with no "session stopped"
        // event, so clients would keep allowing them until their cache expires. The admin service must end
        // and remove the role's sessions itself, publishing events through the outbox, before deleting it.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(s => new { s.ApplicationId, s.RoleId })
            .HasPrincipalKey(r => new { r.ApplicationId, r.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sessions_role_same_application");

        // A session needs an access row for this user and application (DESIGN.md: sign-in requires an active
        // UserApplication). Restrict for the same reason as above: removing access, or the user, must go
        // through a service that ends the sessions and publishes the events.
        builder.HasOne<UserApplication>()
            .WithMany()
            .HasForeignKey(s => new { s.UserId, s.ApplicationId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sessions_user_application");

        // "Stop all sessions of this user" only looks at sessions that haven't ended.
        builder.HasIndex(s => s.UserId).HasFilter("ended_at IS NULL");
    }
}
