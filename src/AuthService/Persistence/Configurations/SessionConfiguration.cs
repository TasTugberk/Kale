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

        // Decision: one open session per user and application. Sign-in ends the user's earlier sessions there,
        // expired ones included (an expired session never gets an end time otherwise), so "not ended" is enough
        // for the database to check. Sign-in's row lock on the access row keeps two sign-ins from colliding; this
        // index makes the rule hold even if code skips that. It also serves "the user's open sessions" (user_id
        // first), which had its own index before.
        builder.HasIndex(s => new { s.UserId, s.ApplicationId }, "ux_sessions_one_open_per_user_and_application")
            .IsUnique()
            .HasFilter("ended_at IS NULL")
            .HasDatabaseName("ux_sessions_one_open_per_user_and_application");

        // The foreign key's own index, unfiltered: deleting an access row looks up all its sessions, ended ones too.
        // Declared here because EF would otherwise count the filtered index above as the foreign key's and drop it.
        builder.HasIndex(s => new { s.UserId, s.ApplicationId }, "ix_sessions_user_id_application_id")
            .HasDatabaseName("ix_sessions_user_id_application_id");
    }
}
