using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

internal sealed class ServerKeyConfiguration : BaseEntityConfiguration<ServerKey>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ServerKey> builder)
    {
        // Stored as text ("Signing"), so the table reads clearly and enum reordering can't change meaning.
        builder.Property(k => k.Use).HasConversion<string>().HasMaxLength(20);
        builder.ToTable(table => table.HasCheckConstraint("ck_server_keys_use", "use IN ('Signing', 'Encryption')"));
        builder.HasIndex(k => k.Use);
    }
}
