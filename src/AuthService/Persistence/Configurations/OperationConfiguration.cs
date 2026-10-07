using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class OperationConfiguration : IEntityTypeConfiguration<Operation>
{
    public void Configure(EntityTypeBuilder<Operation> builder)
    {
        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_operations_name_has_application_prefix", "starts_with(name, application_key || '.')");

            // After the dot: exactly one C# enum member name, e.g. "billing.InvoiceRead".
            table.HasCheckConstraint(
                "ck_operations_name_format", "name ~ '^[a-z][a-z0-9-]*\\.[A-Za-z_][A-Za-z0-9_]*$'");
        });

        builder.Property(o => o.ApplicationKey).HasMaxLength(63);
        builder.Property(o => o.Name).HasMaxLength(200);
        builder.HasIndex(o => new { o.ApplicationId, o.Name }).IsUnique();

        // Referenced by role_operations and operation_implications to keep them inside one application.
        builder.HasAlternateKey(o => new { o.ApplicationId, o.Id });

        // Pointing at (id, key) instead of just id guarantees application_key is this application's real key,
        // which makes the prefix check above meaningful.
        builder.HasOne<Application>()
            .WithMany()
            .HasForeignKey(o => new { o.ApplicationId, o.ApplicationKey })
            .HasPrincipalKey(a => new { a.Id, a.Key })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_operations_application_id_key");

        builder.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
    }
}
