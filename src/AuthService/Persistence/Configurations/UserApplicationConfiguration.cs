using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class UserApplicationConfiguration : IEntityTypeConfiguration<UserApplication>
{
    public void Configure(EntityTypeBuilder<UserApplication> builder)
    {
        builder.HasKey(ua => new { ua.UserId, ua.ApplicationId });

        builder.Property(ua => ua.CreatedAt).IsRequired();
        builder.Property(ua => ua.ModifiedAt).IsRequired();

        builder.HasOne<User>().WithMany().HasForeignKey(ua => ua.UserId).OnDelete(DeleteBehavior.Cascade);

        // Deleting an application that users can still access must be a deliberate, separate step.
        builder.HasOne<Application>()
            .WithMany()
            .HasForeignKey(ua => ua.ApplicationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_user_applications_application");

        // Including application_id keeps the default role inside this application. A null default_role_id
        // skips the check (PostgreSQL's default MATCH SIMPLE). Restrict: the admin service must clear a
        // default before deleting the role, instead of it silently disappearing.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(ua => new { ua.ApplicationId, ua.DefaultRoleId })
            .HasPrincipalKey(r => new { r.ApplicationId, r.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_user_applications_default_role_same_application");
    }
}
