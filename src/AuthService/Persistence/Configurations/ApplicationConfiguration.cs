using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class ApplicationConfiguration : IEntityTypeConfiguration<Application>
{
    public void Configure(EntityTypeBuilder<Application> builder)
    {
        // Lowercase slug, 2-63 chars. No dots, because the key is the prefix in "billing.InvoiceRead".
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_applications_key_format", "key ~ '^[a-z][a-z0-9-]{1,62}$'"));

        builder.Property(a => a.Key).HasMaxLength(63);
        builder.HasIndex(a => a.Key).IsUnique();

        // Operations reference (id, key) together, so the database can check an operation's name prefix.
        // This also means the key can't change while operations use it.
        builder.HasAlternateKey(a => new { a.Id, a.Key });

        builder.Property(a => a.Name).HasMaxLength(200);
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
    }
}
