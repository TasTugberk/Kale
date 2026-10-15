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
        _billing = await _db.AddApplication("billing");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_role_holds_the_operations_granted_to_it_by_qualified_name()
    {
        var accountant = await _db.AddRole(_billing, "Accountant");
        await Grant(accountant, await _db.AddOperation(_billing, "InvoiceRead"));
        await _db.AddOperation(_billing, "PaymentRead"); // exists, but not granted

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task Implications_are_expanded()
    {
        var invoiceRead = await _db.AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await _db.AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await _db.AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_obsolete_granted_operation_grants_nothing_not_even_what_it_implies()
    {
        var invoiceRead = await _db.AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await _db.AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await _db.AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);
        await MarkObsolete(invoiceManage);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_obsolete_implied_operation_is_left_out()
    {
        var invoiceRead = await _db.AddOperation(_billing, "InvoiceRead");
        var invoiceManage = await _db.AddOperation(_billing, "InvoiceManage");
        await Imply(invoiceManage, invoiceRead);
        var accountant = await _db.AddRole(_billing, "Accountant");
        await Grant(accountant, invoiceManage);
        await MarkObsolete(invoiceRead);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceManage"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_chain_through_an_obsolete_operation_stops_there()
    {
        var manage = await _db.AddOperation(_billing, "InvoiceManage");
        var legacy = await _db.AddOperation(_billing, "LegacyInvoice");
        var read = await _db.AddOperation(_billing, "InvoiceRead");
        await Imply(manage, legacy);
        await Imply(legacy, read);
        var accountant = await _db.AddRole(_billing, "Accountant");
        await Grant(accountant, manage);
        await MarkObsolete(legacy);

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, accountant.Id);

        operations.ShouldBe(["billing.InvoiceManage"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_role_with_no_operations_holds_nothing()
    {
        var viewer = await _db.AddRole(_billing, "Viewer");

        var operations = await _effectiveOperations.ForRoleAsync(_billing.Id, viewer.Id);

        operations.ShouldNotBeNull();
        operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_role_of_another_application_is_not_found()
    {
        var hr = await _db.AddApplication("hr");
        var payroll = await _db.AddRole(hr, "Payroll");
        await Grant(payroll, await _db.AddOperation(hr, "SalaryRead"));

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
