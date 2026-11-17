using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace AuthService.Persistence;

// IdentityUserContext (not IdentityDbContext): we use Identity for users only. Roles are our own,
// per-application concept, so Identity's global role tables would only cause confusion.
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options, TimeProvider clock)
    : IdentityUserContext<User, Guid>(options), IDataProtectionKeyContext
{
    // The generated column on OpenIddict's clients that links each one to its application (see OnModelCreating).
    private const string ClientApplicationKey = "ApplicationKey";

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
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<ServerKey> ServerKeys => Set<ServerKey>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Data Protection's key ring, shared by all instances (it encrypts the server keys).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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

        // OpenIddict's tables (clients, authorizations, scopes, tokens), with our Guid keys. Its default names
        // would become open_iddict_*; openiddict_* matches the design document.
        builder.UseOpenIddict<Guid>();
        builder.Entity<OpenIddictEntityFrameworkCoreApplication<Guid>>().ToTable("openiddict_applications");
        builder.Entity<OpenIddictEntityFrameworkCoreAuthorization<Guid>>().ToTable("openiddict_authorizations");
        builder.Entity<OpenIddictEntityFrameworkCoreScope<Guid>>().ToTable("openiddict_scopes");
        builder.Entity<OpenIddictEntityFrameworkCoreToken<Guid>>().ToTable("openiddict_tokens");

        // Every OpenIddict client belongs to exactly one Application: its web client has the key as client_id, and
        // its desktop client (when switched on, see DesktopClient) "{key}.desktop". Keys can't contain '.', so the
        // part before the first '.' is always the key: a generated column holds it, and the foreign key checks it.
        // The check allows only these two client ids, so with client_id unique an application has at most these two
        // clients. Both never change. Required: PostgreSQL skips a foreign key check when the column is NULL.
        var clients = builder.Entity<OpenIddictEntityFrameworkCoreApplication<Guid>>();
        clients.ToTable(table => table.HasCheckConstraint(
            "ck_openiddict_applications_client_id",
            $"client_id = application_key OR client_id = application_key || '{DesktopClient.Suffix}'"));
        clients.Property(client => client.ClientId).IsRequired();
        clients.Property<string>(ClientApplicationKey)
            .IsRequired()
            .HasMaxLength(63)
            .HasComputedColumnSql("split_part(client_id, '.', 1)", stored: true);
        clients
            .HasOne<Application>()
            .WithMany()
            .HasForeignKey(ClientApplicationKey)
            .HasPrincipalKey(application => application.Key)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_openiddict_applications_application");
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
        builder.ApplyConfiguration(new SessionConfiguration());
        builder.ApplyConfiguration(new ServerKeyConfiguration());
        builder.ApplyConfiguration(new OutboxMessageConfiguration());
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
