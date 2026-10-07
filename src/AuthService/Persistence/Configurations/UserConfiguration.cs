using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthService.Persistence.Configurations;

/// <summary>Our columns on Identity's user table. Identity itself maps the rest (password hash, lockout, ...).</summary>
internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.ModifiedAt).IsRequired();

        // Identity names these indexes "UserNameIndex" and "EmailIndex"; mixed case needs quoting in SQL.
        builder.HasIndex(u => u.NormalizedUserName).HasDatabaseName("ix_users_normalized_user_name");
        builder.HasIndex(u => u.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");

        // Ids come from code (UUID v7), like BaseEntity.
        builder.Property(u => u.Id).ValueGeneratedNever();
    }
}
