using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class OperationImplicationConfiguration : IEntityTypeConfiguration<OperationImplication>
{
    public void Configure(EntityTypeBuilder<OperationImplication> builder)
    {
        builder.HasKey(i => new { i.OperationId, i.ImpliedOperationId });

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_operation_implications_not_self", "operation_id <> implied_operation_id"));

        // Both foreign keys include application_id, so both operations must belong to the same application.
        builder.HasOne<Operation>()
            .WithMany()
            .HasForeignKey(i => new { i.ApplicationId, i.OperationId })
            .HasPrincipalKey(o => new { o.ApplicationId, o.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operation_implications_operation_same_application");

        builder.HasOne<Operation>()
            .WithMany()
            .HasForeignKey(i => new { i.ApplicationId, i.ImpliedOperationId })
            .HasPrincipalKey(o => new { o.ApplicationId, o.Id })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operation_implications_implied_same_application");
    }
}
