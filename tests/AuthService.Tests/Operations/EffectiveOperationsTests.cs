using AuthService.Domain;
using AuthService.Operations;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.Operations;

/// <summary>What RoleService.GetEffectiveOperations returns: a role's operations, implications expanded.</summary>
[Collection(UsesPostgres.Name)]
public sealed class EffectiveOperationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private EffectiveOperations _effectiveOperations = null!;
    private Application _billing = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
        _effectiveOperations = new EffectiveOperations(_db);
        _billing = await AddApplication("billing");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_role_holds_the_operations_granted_to_it_by_qualified_name()
    {
        var accountant = await AddRole(_billing, "Accountant");
        await Grant(accountant, await AddOperation(_billing, "InvoiceRead"));
        await AddOperation(_billing, "PaymentRead"); // exists, but not granted

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task Implications_are_expanded()
    {
        var invoiceRead = await AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_obsolete_granted_operation_and_what_it_implies_through_it_are_left_out()
    {
        var invoiceRead = await AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);
        await MarkObsolete(invoiceManage);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_obsolete_implied_operation_is_left_out()
    {
        var invoiceRead = await AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);
        await MarkObsolete(invoiceRead);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceManage"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_role_with_no_operations_holds_nothing()
    {
        var viewer = await AddRole(_billing, "Viewer");

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, viewer.Id);

        operations.ShouldNotBeNull();
        operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_of_another_application_is_not_found()
    {
        var hr = await AddApplication("hr");
        var payroll = await AddRole(hr, "Payroll");
        await Grant(payroll, await AddOperation(hr, "SalaryRead"));

        // billing asks about hr's role: answer as if it doesn't exist, so billing learns nothing about hr.
        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, payroll.Id);

        operations.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_role_is_not_found()
    {
        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, Guid.CreateVersion7());

        operations.ShouldBeNull();
    }

    // Local helpers: this branch is stacked on #12, which doesn't have the shared TestData from #11 yet.
    // They move to TestData once both are on main.

    private async Task<Application> AddApplication(string key)
    {
        var application = new Application { Key = key, Name = key };
        _db.Applications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<Operation> AddOperation(Application application, string member)
    {
        var operation = new Operation { ApplicationId = application.Id, ApplicationKey = application.Key, Name = $"{application.Key}.{member}" };
        _db.Operations.Add(operation);
        await _db.SaveChangesAsync();
        return operation;
    }

    private async Task<Role> AddRole(Application application, string name)
    {
        var role = new Role { ApplicationId = application.Id, Name = name };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        return role;
    }

    private async Task Grant(Role role, Operation operation)
    {
        _db.RoleOperations.Add(new RoleOperation { ApplicationId = role.ApplicationId, RoleId = role.Id, OperationId = operation.Id });
        await _db.SaveChangesAsync();
    }

    private async Task Imply(Operation operation, Operation implied)
    {
        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = operation.ApplicationId, OperationId = operation.Id, ImpliedOperationId = implied.Id,
        });
        await _db.SaveChangesAsync();
    }

    private async Task MarkObsolete(Operation operation)
    {
        operation.ObsoletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
    }
}
