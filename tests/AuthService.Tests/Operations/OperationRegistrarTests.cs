using AuthService.Domain;
using AuthService.Operations;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Operations;

/// <summary>What OperationRegistration.RegisterOperations does with an app's enum on startup (DESIGN.md registration flow).</summary>
[Collection(UsesPostgres.Name)]
public sealed class OperationRegistrarTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private Application _billing = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _billing = await AddApplication("billing");
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
    public async Task An_operation_no_longer_sent_is_marked_obsolete_not_deleted()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await Register(_billing, Op("InvoiceRead"));

        result.Obsoleted.ShouldBe(["billing.LegacyExport"]);
        var legacy = await _db.Operations.AsNoTracking().SingleAsync(o => o.Name == "billing.LegacyExport");
        legacy.ObsoletedAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public async Task An_obsolete_operation_sent_again_is_reactivated()
    {
        await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));
        await Register(_billing, Op("InvoiceRead"));

        var result = await Register(_billing, Op("InvoiceRead"), Op("LegacyExport"));

        result.Reactivated.ShouldBe(["billing.LegacyExport"]);
        var legacy = await _db.Operations.AsNoTracking().SingleAsync(o => o.Name == "billing.LegacyExport");
        legacy.ObsoletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Implications_are_replaced_by_the_ones_sent()
    {
        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "InvoiceRead"));

        await Register(_billing, Op("InvoiceRead"), Op("PaymentRead"), Op("Manage", implies: "PaymentRead"));

        (await Implications(_billing)).ShouldBe([("billing.Manage", "billing.PaymentRead")]);
    }

    [Fact]
    public async Task Another_applications_operations_are_untouched()
    {
        var hr = await AddApplication("hr");
        await Register(hr, Op("SalaryRead"));

        await Register(_billing, Op("InvoiceRead"));

        (await OperationNames(hr)).ShouldBe(["hr.SalaryRead"]);
        (await _db.Operations.AsNoTracking().SingleAsync(o => o.Name == "hr.SalaryRead")).ObsoletedAt.ShouldBeNull();
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
    public async Task An_invalid_name_is_rejected(string name)
    {
        await ShouldReject(() => Register(_billing, Op(name)), "name");
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
            await new OperationRegistrar(instanceDb, _clock).RegisterAsync(_billing.Id, operations);
        });
        await Task.WhenAll(instances);

        (await OperationNames(_billing)).ShouldBe(
            ["billing.InvoiceManage", "billing.InvoiceRead", "billing.PaymentRead"], ignoreOrder: true);
        (await Implications(_billing)).Count.ShouldBe(1);
    }

    // ----- Helpers -----

    private static OperationDeclaration Op(string name, params string[] implies) => new(name, implies);

    private Task<RegistrationResult> Register(Application application, params OperationDeclaration[] operations) =>
        new OperationRegistrar(_db, _clock).RegisterAsync(application.Id, operations);

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

    // Local helper: this branch is stacked on #15/#12, which don't have #11's shared TestData yet.
    private async Task<Application> AddApplication(string key)
    {
        var application = new Application { Key = key, Name = key };
        _db.Applications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }
}
