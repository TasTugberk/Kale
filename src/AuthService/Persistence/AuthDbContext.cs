using AuthService.Domain;
using AuthService.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationImplication> OperationImplications => Set<OperationImplication>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleOperation> RoleOperations => Set<RoleOperation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ApplicationConfiguration());
        modelBuilder.ApplyConfiguration(new OperationConfiguration());
        modelBuilder.ApplyConfiguration(new OperationImplicationConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RoleOperationConfiguration());
    }
}
