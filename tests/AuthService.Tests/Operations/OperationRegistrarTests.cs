using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Operations;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Operations;

/// <summary>
/// What OperationRegistration.RegisterOperations does with an app's enum, sent on startup and every few minutes
/// (DESIGN.md registration flow). Operations no registration has listed for <see cref="ObsoleteAfter"/> become
/// obsolete, so two versions of an app running side by side (a rolling deployment, a rollback) keep both sets.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class OperationRegistrarTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ObsoleteAfter = TimeSpan.FromMinutes(30);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private Application _billing = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _billing = await _db.AddApplication("billing");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ----- Upsert -----

    [Fact]
    public async Task First_registration_adds_every_operation_with_the_application_prefix()
    {
        var result = await Register(_billing, Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"));

        result.Added.ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
        (await OperationNames(_billing)).ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
        (await Implications(_billing)).ShouldBe([("billing.InvoiceManage", "billing.InvoiceRead")]);
    }

    [Fact]
    public async Task Registering_the_same_enum_again_changes_nothing()
    {
        await Register(_billing, Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"));

        var result = await Register(_billing, Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"));

        result.Added.ShouldBeEmpty();
        result.Reactivated.ShouldBeEmpty();
        result.Obsoleted.ShouldBeEmpty();
        result.Unchanged.ShouldBe(2);
        (await _db.Operations.CountAsync()).ShouldBe(2);
        (await Implications(_billing)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_operation_no_longer_sent_stays_active_during_the_grace_period()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(ObsoleteAfter);

        var result = await Register(_billing, Op("InvoiceRead"));

        result.Obsoleted.ShouldBeEmpty();
        (await Operation("billing.LegacyExport")).ObsoletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task An_operation_no_registration_listed_for_the_grace_period_is_marked_obsolete_not_deleted()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(ObsoleteAfter + TimeSpan.FromSeconds(1));

        var result = await Register(_billing, Op("InvoiceRead"));

        result.Obsoleted.ShouldBe(["billing.LegacyExport"]);
        (await Operation("billing.LegacyExport")).ObsoletedAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task The_grace_period_counts_from_the_last_registration_that_listed_the_operation()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(TimeSpan.FromMinutes(20));
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(TimeSpan.FromMinutes(20));

        var result = await Register(_billing, Op("InvoiceRead"));

        result.Obsoleted.ShouldBeEmpty("listed 20 minutes ago");
        (await Operation("billing.LegacyExport")).LastRegisteredAt.ShouldBe(Start.AddMinutes(20));
    }

    [Fact]
    public async Task An_obsolete_operation_sent_again_is_reactivated()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(ObsoleteAfter + TimeSpan.FromSeconds(1));
        await Register(_billing, Op("InvoiceRead"));

        var result = await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));

        result.Reactivated.ShouldBe(["billing.LegacyExport"]);
        (await Operation("billing.LegacyExport")).ObsoletedAt.ShouldBeNull();
    }

    // ----- Two versions running side by side -----

    [Fact]
    public async Task During_a_rolling_deployment_an_old_instance_registering_last_does_not_obsolete_the_new_operations()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));      // v1
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"));     // v2 starts
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Register(_billing, Op("InvoiceRead"), Op("LegacyExport")); // a v1 instance restarts

        result.Obsoleted.ShouldBeEmpty();
        (await Operation("billing.InvoiceExport")).ObsoletedAt.ShouldBeNull();
        (await Operation("billing.LegacyExport")).ObsoletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Once_only_the_new_version_registers_the_old_versions_extras_become_obsolete_after_the_grace_period()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));      // v1, last seen
        await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"));     // v2
        for (var i = 0; i < 6; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            (await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"))).Obsoleted.ShouldBeEmpty();
        }

        _clock.Advance(TimeSpan.FromMinutes(5));
        var result = await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"));

        result.Obsoleted.ShouldBe(["billing.LegacyExport"]);
        (await Operation("billing.InvoiceExport")).ObsoletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task After_a_rollback_the_newer_versions_extras_become_obsolete_after_the_grace_period()
    {
        await Register(_billing, Op("InvoiceRead"));                          // v1
        await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"));     // v2, then rolled back
        _clock.Advance(TimeSpan.FromMinutes(10));
        (await Register(_billing, Op("InvoiceRead"))).Obsoleted.ShouldBeEmpty("v2 was seen 10 minutes ago");

        _clock.Advance(ObsoleteAfter);
        var result = await Register(_billing, Op("InvoiceRead"));

        result.Obsoleted.ShouldBe(["billing.InvoiceExport"]);
        (await Operation("billing.InvoiceRead")).ObsoletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task An_operations_implications_follow_the_latest_registration_that_lists_it()
    {
        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "InvoiceRead"));

        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "PaymentRead"));

        (await Implications(_billing)).ShouldBe([("billing.Manage", "billing.PaymentRead")]);
    }

    [Fact]
    public async Task Implications_of_an_operation_the_registration_does_not_list_are_kept()
    {
        // v2 adds Admin, which implies InvoiceRead; a v1 instance registering afterwards doesn't know Admin.
        await Register(_billing, Op("InvoiceRead"), Op("Admin", implies: "InvoiceRead"));

        await Register(_billing, Op("InvoiceRead"));

        (await Implications(_billing)).ShouldBe([("billing.Admin", "billing.InvoiceRead")]);
    }

    [Fact]
    public async Task Another_applications_operations_are_untouched()
    {
        var hr = await _db.AddApplication("hr");
        await Register(hr, Op("SalaryRead"));

        await Register(_billing, Op("InvoiceRead"));

        (await OperationNames(hr)).ShouldBe(["hr.SalaryRead"]);
        (await _db.Operations.AsNoTracking().SingleAsync(o => o.Name == "hr.SalaryRead")).ObsoletedAt.ShouldBeNull();
    }

    // ----- Invalidation events (roles' effective operations changed) -----

    [Fact]
    public async Task Registering_the_same_enum_again_queues_no_events_and_only_moves_the_registration_time()
    {
        await _db.AddRole(_billing, "Clerk");
        await Register(_billing, Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"));
        await ClearOutbox();
        var before = await _db.Operations.AsNoTracking().OrderBy(o => o.Name).ToListAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        await Register(_billing, Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"));

        (await _db.OutboxMessages.CountAsync()).ShouldBe(0);
        var after = await _db.Operations.AsNoTracking().OrderBy(o => o.Name).ToListAsync();
        after.Select(o => o.LastRegisteredAt).ShouldAllBe(t => t == Start.AddMinutes(5));
        after.Select(o => o.ObsoletedAt).ShouldBe(before.Select(o => o.ObsoletedAt));
        (await Implications(_billing)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Adding_an_operation_nothing_implies_queues_no_events()
    {
        await _db.AddRole(_billing, "Clerk");
        await Register(_billing, Op("InvoiceRead"));
        await ClearOutbox();

        await Register(_billing, Op("InvoiceRead"), Op("InvoiceExport"));

        (await _db.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Changed_implications_queue_RoleEdited_for_every_role_of_the_application()
    {
        var clerk = await _db.AddRole(_billing, "Clerk");
        var manager = await _db.AddRole(_billing, "Manager");
        var hr = await _db.AddApplication("hr");
        await _db.AddRole(hr, "Recruiter");
        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "InvoiceRead"));
        await ClearOutbox();

        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "PaymentRead"));

        (await RoleEditedEvents()).ShouldBe([(clerk.Id, _billing.Id), (manager.Id, _billing.Id)], ignoreOrder: true);
    }

    [Fact]
    public async Task An_operation_becoming_obsolete_queues_RoleEdited()
    {
        var clerk = await _db.AddRole(_billing, "Clerk");
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        await ClearOutbox();
        _clock.Advance(ObsoleteAfter + TimeSpan.FromSeconds(1));

        await Register(_billing, Op("InvoiceRead"));

        (await RoleEditedEvents()).ShouldBe([(clerk.Id, (Guid?)_billing.Id)]);
    }

    [Fact]
    public async Task An_operation_becoming_active_again_queues_RoleEdited()
    {
        var clerk = await _db.AddRole(_billing, "Clerk");
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(ObsoleteAfter + TimeSpan.FromSeconds(1));
        await Register(_billing, Op("InvoiceRead"));
        await ClearOutbox();

        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));

        (await RoleEditedEvents()).ShouldBe([(clerk.Id, (Guid?)_billing.Id)]);
    }

    // ----- Validation (INVALID_ARGUMENT) -----

    [Fact]
    public async Task An_empty_list_is_rejected_because_it_would_remove_every_permission()
    {
        await Register(_billing, Op("InvoiceRead"));

        await ShouldReject(() => Register(_billing), "empty");

        (await _db.Operations.AsNoTracking().SingleAsync()).ObsoletedAt.ShouldBeNull("nothing was obsoleted");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Invoice Read")]
    [InlineData("billing.InvoiceRead")] // the prefix is added by the service, never sent
    [InlineData("1Invoice")]
    [InlineData("InvoiceRead\n")] // .NET's $ also matches before a final newline; validation must not let it through
    public async Task An_invalid_name_is_rejected(string name)
    {
        await ShouldReject(() => Register(_billing, Op(name)), "name");
    }

    [Fact]
    public async Task A_name_longer_than_100_characters_is_rejected_before_the_database_sees_it()
    {
        await ShouldReject(() => Register(_billing, Op(new string('A', 101))), "100");
    }

    [Fact]
    public async Task A_name_of_100_characters_is_accepted()
    {
        var result = await Register(_billing, Op(new string('A', 100)));

        result.Added.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_duplicate_name_is_rejected()
    {
        await ShouldReject(() => Register(_billing, Op("InvoiceRead"), Op("InvoiceRead")), "InvoiceRead");
    }

    [Fact]
    public async Task Implying_a_name_that_is_not_in_the_request_is_rejected()
    {
        await ShouldReject(() => Register(_billing, Op("InvoiceManage", implies: "InvoiceRead")), "InvoiceRead");
    }

    [Fact]
    public async Task A_cycle_of_implications_is_rejected_with_its_path()
    {
        await ShouldReject(
            () => Register(_billing, Op("A", implies: "B"), Op("B", implies: "A")),
            "A -> B -> A");
    }

    [Fact]
    public async Task A_rejected_registration_changes_nothing()
    {
        await Register(_billing, Op("InvoiceRead"));

        await ShouldReject(() => Register(_billing, Op("PaymentRead"), Op("Bad Name")), "name");

        (await OperationNames(_billing)).ShouldBe(["billing.InvoiceRead"]);
        (await _db.Operations.AsNoTracking().SingleAsync()).ObsoletedAt.ShouldBeNull();
    }

    // ----- Several instances starting at once -----

    [Fact]
    public async Task Concurrent_registrations_of_the_same_enum_end_with_one_set_of_operations()
    {
        var databaseName = _db.Database.GetDbConnection().Database;
        var operations = new[] { Op("InvoiceRead"), Op("InvoiceManage", implies: "InvoiceRead"), Op("PaymentRead") };

        // Each "instance" has its own context and connection, like separate app processes.
        var instances = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var instanceDb = postgres.CreateDbContext(databaseName, _clock);
            return await new OperationRegistrar(instanceDb, _clock, Settings).RegisterAsync(_billing.Id, operations);
        });
        var results = await Task.WhenAll(instances);

        // They took turns: exactly one added everything, the rest found it already there.
        results.Count(r => r.Added.Count == 3).ShouldBe(1);
        results.Count(r => r.Unchanged == 3 && r.Added.Count == 0).ShouldBe(7);

        (await OperationNames(_billing)).ShouldBe(
            ["billing.InvoiceManage", "billing.InvoiceRead", "billing.PaymentRead"], ignoreOrder: true);
        (await Implications(_billing)).Count.ShouldBe(1);
    }

    // ----- Helpers -----

    private static readonly OperationRegistrationSettings Settings = new(ObsoleteAfter);

    private static OperationDeclaration Op(string name, params string[] implies) => new(name, implies);

    private Task<RegistrationResult> Register(Application application, params OperationDeclaration[] operations) =>
        new OperationRegistrar(_db, _clock, Settings).RegisterAsync(application.Id, operations);

    private Task<Operation> Operation(string name) => _db.Operations.AsNoTracking().SingleAsync(o => o.Name == name);

    private async Task ClearOutbox() => await _db.OutboxMessages.ExecuteDeleteAsync();

    private async Task<List<(Guid RoleId, Guid? ApplicationId)>> RoleEditedEvents()
    {
        var messages = await _db.OutboxMessages.AsNoTracking().ToListAsync();
        messages.ShouldAllBe(m => m.Type == nameof(InvalidationEvent.EventOneofCase.RoleEdited));
        return messages
            .Select(m => (Guid.Parse(InvalidationEvent.Parser.ParseFrom(m.Payload).RoleEdited.RoleId), m.ApplicationId))
            .ToList();
    }

    private static async Task ShouldReject(Func<Task> register, string messagePart)
    {
        var error = await Should.ThrowAsync<InvalidRegistrationException>(register);
        error.Message.ShouldContain(messagePart);
    }

    private async Task<List<string>> OperationNames(Application application) =>
        await _db.Operations.AsNoTracking()
            .Where(o => o.ApplicationId == application.Id)
            .Select(o => o.Name)
            .ToListAsync();

    private async Task<List<(string, string)>> Implications(Application application)
    {
        var rows = await (
                from implication in _db.OperationImplications.AsNoTracking()
                join source in _db.Operations on implication.OperationId equals source.Id
                join target in _db.Operations on implication.ImpliedOperationId equals target.Id
                where implication.ApplicationId == application.Id
                select new { Source = source.Name, Target = target.Name })
            .ToListAsync();
        return rows.Select(r => (r.Source, r.Target)).ToList();
    }
}
