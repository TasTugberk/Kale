using AuthService.Domain;
using AuthService.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options, TimeProvider clock) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationImplication> OperationImplications => Set<OperationImplication>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleOperation> RoleOperations => Set<RoleOperation>();

    // The parameterless SaveChanges overloads call these two, so overriding them covers every save.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SetTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        SetTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ApplicationConfiguration());
        modelBuilder.ApplyConfiguration(new OperationConfiguration());
        modelBuilder.ApplyConfiguration(new OperationImplicationConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RoleOperationConfiguration());
    }

    private void SetTimestamps()
    {
        var now = clock.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.ModifiedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.ModifiedAt = now;

                // Update() on a detached object marks every column as changed, including a CreatedAt
                // that was never loaded. Never write it on an update.
                entry.Property(e => e.CreatedAt).IsModified = false;
            }
        }
    }
}
