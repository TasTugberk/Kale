using AuthService.Domain;
using AuthService.Persistence.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

// IdentityUserContext (not IdentityDbContext): we use Identity for users only. Roles are our own,
// per-application concept, so Identity's global role tables would only cause confusion.
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options, TimeProvider clock)
    : IdentityUserContext<User, Guid>(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationImplication> OperationImplications => Set<OperationImplication>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleOperation> RoleOperations => Set<RoleOperation>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<UserGroup> UserGroups => Set<UserGroup>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<GroupRole> GroupRoles => Set<GroupRole>();
    public DbSet<UserApplication> UserApplications => Set<UserApplication>();

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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity's own tables first; our configurations then adjust them.
        base.OnModelCreating(builder);

        // Identity would name these asp_net_users, asp_net_user_claims, ...; plain names read better in SQL.
        // Renamed before our configurations run, so foreign keys to users get names based on "users" too.
        builder.Entity<User>().ToTable("users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        // No passkeys table: Identity only adds one when IdentityOptions.Stores.SchemaVersion enables it.
        // If Phase 3 changes Identity options, The_apps_database_model_matches_the_migrations fails until a
        // migration covers it.

        builder.ApplyConfiguration(new ApplicationConfiguration());
        builder.ApplyConfiguration(new OperationConfiguration());
        builder.ApplyConfiguration(new OperationImplicationConfiguration());
        builder.ApplyConfiguration(new RoleConfiguration());
        builder.ApplyConfiguration(new RoleOperationConfiguration());

        builder.ApplyConfiguration(new UserConfiguration());
        builder.ApplyConfiguration(new GroupConfiguration());
        builder.ApplyConfiguration(new UserGroupConfiguration());
        builder.ApplyConfiguration(new UserRoleConfiguration());
        builder.ApplyConfiguration(new GroupRoleConfiguration());
        builder.ApplyConfiguration(new UserApplicationConfiguration());
    }

    private void SetTimestamps()
    {
        var now = clock.GetUtcNow();

        foreach (var entry in ChangeTracker.Entries<IHasTimestamps>())
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
