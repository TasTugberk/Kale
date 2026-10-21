using System.ComponentModel.DataAnnotations;
using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
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
        _editor = new RoleEditor(_db, new SessionStopper(_db, TimeProvider.System));

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

    // ----- Creating, renaming, deleting -----

    [Fact]
    public async Task Creating_adds_a_role_and_the_same_name_twice_in_one_application_is_refused()
    {
        var role = await _editor.CreateAsync(_billing.Id, "Auditor", "Reads everything");

        role.Name.ShouldBe("Auditor");
        role.Description.ShouldBe("Reads everything");
        (await Should.ThrowAsync<ValidationException>(() => _editor.CreateAsync(_billing.Id, "Auditor", null)))
            .Message.ShouldContain("already has a role");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_role_needs_a_name(string name)
    {
        await Should.ThrowAsync<ValidationException>(() => _editor.CreateAsync(_billing.Id, name, null));
    }

    [Fact]
    public async Task Renaming_changes_the_name_and_description_but_not_to_a_taken_name()
    {
        await _editor.RenameAsync(_accountant.Id, "Bookkeeper", "Books invoices");

        await using var fresh = Fresh();
        var renamed = await fresh.Roles.SingleAsync(r => r.Id == _accountant.Id);
        renamed.Name.ShouldBe("Bookkeeper");
        renamed.Description.ShouldBe("Books invoices");
        await _editor.CreateAsync(_billing.Id, "Auditor", null);
        await Should.ThrowAsync<ValidationException>(() => _editor.RenameAsync(_accountant.Id, "Auditor", null));
    }

    [Fact]
    public async Task Deleting_a_role_stops_and_removes_its_sessions_and_its_assignments()
    {
        // Sessions restrict deleting their role (a database cascade would send no "session stopped" event), so
        // the service ends them itself, with the events, in the same transaction.
        var (user, session) = await AddUserWithSession(_accountant);
        var otherRole = await _db.AddRole(_billing, "Auditor");
        var (_, otherSession) = await AddUserWithSession(otherRole);
        var access = await _db.UserApplications.SingleAsync(a => a.UserId == user.Id);
        access.DefaultRoleId = _accountant.Id;
        await _db.SaveChangesAsync();

        // Otherwise EF would clear the default role of the rows it still tracks from the setup by itself.
        _db.ChangeTracker.Clear();
        await _editor.DeleteAsync(_accountant.Id);

        await using var fresh = Fresh();
        (await fresh.Roles.AnyAsync(r => r.Id == _accountant.Id)).ShouldBeFalse();
        (await fresh.Sessions.AnyAsync(s => s.Id == session.Id)).ShouldBeFalse();
        (await fresh.Sessions.AnyAsync(s => s.Id == otherSession.Id)).ShouldBeTrue("another role's session stays");
        (await fresh.UserRoles.AnyAsync(ur => ur.RoleId == _accountant.Id)).ShouldBeFalse();
        (await fresh.UserApplications.SingleAsync(a => a.UserId == user.Id)).DefaultRoleId.ShouldBeNull();
        (await QueuedEvents(fresh)).Select(e => e.SessionStopped?.SessionId).ShouldContain(session.Id.ToString());
    }

    [Fact]
    public async Task A_sign_in_with_the_role_still_in_progress_finishes_first_and_its_session_goes_too()
    {
        var user = await _db.AddUser("late");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Id });
        await _db.SaveChangesAsync();

        // A sign-in saving a session with this role, not committed yet.
        await using var signIn = Fresh();
        await using var transaction = await signIn.Database.BeginTransactionAsync();
        var lateSession = new Session { UserId = user.Id, ApplicationId = _billing.Id, RoleId = _accountant.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(8) };
        signIn.Sessions.Add(lateSession);
        await signIn.SaveChangesAsync();

        var delete = _editor.DeleteAsync(_accountant.Id);
        await Task.Delay(500);
        delete.IsCompleted.ShouldBeFalse("deleting must wait for the sign-in");
        await transaction.CommitAsync();
        await delete;

        await using var fresh = Fresh();
        (await fresh.Sessions.AnyAsync(s => s.Id == lateSession.Id)).ShouldBeFalse();
        (await fresh.Roles.AnyAsync(r => r.Id == _accountant.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Deleting_an_unknown_role_reports_it()
    {
        // E.g. the second of two deletes sent at once: an answer (not found), not an error.
        (await _editor.DeleteAsync(Guid.CreateVersion7())).ShouldBeFalse();
    }

    [Theory]
    [InlineData(101, 0, "at most 100")]
    [InlineData(10, 501, "at most 500")]
    public async Task Names_and_descriptions_have_limits(int nameLength, int descriptionLength, string reason)
    {
        var error = await Should.ThrowAsync<ValidationException>(() =>
            _editor.CreateAsync(_billing.Id, new string('n', nameLength), new string('d', descriptionLength)));

        error.Message.ShouldContain(reason);
    }

    [Fact]
    public async Task Names_and_descriptions_are_trimmed_and_a_blank_description_is_none()
    {
        var role = await _editor.CreateAsync(_billing.Id, "  Auditor  ", "  Reads everything  ");
        var blank = await _editor.CreateAsync(_billing.Id, "Viewer", "   ");

        role.Name.ShouldBe("Auditor");
        role.Description.ShouldBe("Reads everything");
        blank.Description.ShouldBeNull();
    }

    [Fact]
    public async Task Two_creates_of_the_same_name_at_once_end_in_one_role_and_one_already_has()
    {
        // E.g. a double click in the admin UI: both may pass the "already has?" check before either saves.
        var attempts = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = Fresh();
            try
            {
                await new RoleEditor(db, new SessionStopper(db, TimeProvider.System)).CreateAsync(_billing.Id, "Auditor", null);
                return "created";
            }
            catch (ValidationException exception)
            {
                return exception.Message;
            }
        });

        var results = await Task.WhenAll(attempts);

        results.Count(r => r == "created").ShouldBe(1);
        results.Single(r => r != "created").ShouldContain("already has a role");
    }

    // A user with access to the role's application, holding the role, signed in with it.
    private async Task<(User User, Session Session)> AddUserWithSession(Role role)
    {
        var user = await _db.AddUser($"user-{Guid.CreateVersion7():N}");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = role.ApplicationId });
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        var session = new Session { UserId = user.Id, ApplicationId = role.ApplicationId, RoleId = role.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(8) };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        return (user, session);
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
