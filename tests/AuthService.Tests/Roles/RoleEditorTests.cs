using System.ComponentModel.DataAnnotations;
using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.Roles;

/// <summary>
/// Editing a role's operations (DESIGN.md: "editing a role must take effect on active sessions immediately"):
/// the change and a "role edited" event are saved together, so client apps reload the role's operations.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class RoleEditorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private RoleEditor _editor = null!;
    private Application _billing = null!;
    private Role _accountant = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
        _editor = new RoleEditor(_db);

        _billing = await _db.AddApplication("billing");
        await _db.AddOperation(_billing, "InvoiceRead");
        await _db.AddOperation(_billing, "InvoiceManage");
        _accountant = await _db.AddRole(_billing, "Accountant");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Setting_a_roles_operations_replaces_them_and_queues_role_edited()
    {
        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead"]);

        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceManage"]);

        await using var fresh = Fresh();
        (await GrantedNames(fresh)).ShouldBe(["billing.InvoiceManage"]);
        var events = await QueuedEvents(fresh);
        events.Count.ShouldBe(2);
        events.ShouldAllBe(e => e.RoleEdited.RoleId == _accountant.Id.ToString());
    }

    [Fact]
    public async Task Operations_the_role_already_has_are_kept()
    {
        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead"]);

        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead", "billing.InvoiceManage"]);

        await using var fresh = Fresh();
        (await GrantedNames(fresh)).ShouldBe(["billing.InvoiceRead", "billing.InvoiceManage"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_empty_list_takes_every_operation_away()
    {
        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead", "billing.InvoiceManage"]);

        await _editor.SetOperationsAsync(_accountant.Id, []);

        await using var fresh = Fresh();
        (await GrantedNames(fresh)).ShouldBeEmpty();
        (await QueuedEvents(fresh)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Two_edits_of_the_same_role_take_turns()
    {
        // Without this, two admins saving at once would each compute their changes from the same old grants,
        // and the role would end up with both sets. Another transaction holding the role's lock stands in for
        // the first admin. The edit only removes grants: adding one would wait anyway, on the foreign key's
        // check of the role row, and hide a missing lock.
        await _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead"]);
        await using var otherAdmin = Fresh();
        await using var transaction = await otherAdmin.Database.BeginTransactionAsync();
        await otherAdmin.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {_accountant.Id} FOR UPDATE");

        var edit = _editor.SetOperationsAsync(_accountant.Id, []);
        await Task.Delay(500);
        edit.IsCompleted.ShouldBeFalse("the edit must wait for the other transaction's lock on the role");

        await transaction.CommitAsync();
        await edit;
    }

    [Fact]
    public async Task An_operation_of_another_application_is_refused_and_nothing_changes()
    {
        var hr = await _db.AddApplication("hr");
        await _db.AddOperation(hr, "PayrollRead");

        var error = await Should.ThrowAsync<ValidationException>(() =>
            _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceRead", "hr.PayrollRead"]));

        error.Message.ShouldContain("hr.PayrollRead");
        await using var fresh = Fresh();
        (await fresh.RoleOperations.CountAsync()).ShouldBe(0);
        (await fresh.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_or_obsolete_operation_is_refused()
    {
        var invoiceManage = await _db.Operations.SingleAsync(o => o.Name == "billing.InvoiceManage");
        invoiceManage.ObsoletedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        (await Should.ThrowAsync<ValidationException>(() => _editor.SetOperationsAsync(_accountant.Id, ["billing.Nope"])))
            .Message.ShouldContain("billing.Nope");
        (await Should.ThrowAsync<ValidationException>(() => _editor.SetOperationsAsync(_accountant.Id, ["billing.InvoiceManage"])))
            .Message.ShouldContain("billing.InvoiceManage");
    }

    [Fact]
    public async Task An_unknown_role_is_an_error()
    {
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _editor.SetOperationsAsync(Guid.CreateVersion7(), ["billing.InvoiceRead"]));
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(_db.Database.GetDbConnection().Database);

    private Task<List<string>> GrantedNames(AuthDbContext db) =>
        db.RoleOperations.Where(ro => ro.RoleId == _accountant.Id)
            .Join(db.Operations, ro => ro.OperationId, o => o.Id, (_, o) => o.Name)
            .ToListAsync();

    private static async Task<List<InvalidationEvent>> QueuedEvents(AuthDbContext db) =>
        (await db.OutboxMessages.OrderBy(m => m.Id).ToListAsync())
            .Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload))
            .ToList();
}
