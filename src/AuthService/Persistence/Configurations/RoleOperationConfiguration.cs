using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class RoleOperationConfiguration : IEntityTypeConfiguration<RoleOperation>
{
    public void Configure(EntityTypeBuilder<RoleOperation> builder)
    {
        builder.HasKey(ro => new { ro.RoleId, ro.OperationId });

        // Both foreign keys include application_id, so the database rejects a role and an operation
        // from different applications.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(ro => new { ro.ApplicationId, ro.RoleId })
            .HasPrincipalKey(r => new { r.ApplicationId, r.Id })
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_role_operations_role_same_application");

        // Operations are never deleted (only marked obsolete), so block it outright.
        builder.HasOne<Operation>()
            .WithMany()
            .HasForeignKey(ro => new { ro.ApplicationId, ro.OperationId })
            .HasPrincipalKey(o => new { o.ApplicationId, o.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_role_operations_operation_same_application");
    }
}
