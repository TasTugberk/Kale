using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Tests.Persistence;

/// <summary>
/// The database itself must reject data that mixes applications (DESIGN.md: "cross-application checks must be
/// real database constraints"). Each test checks the PostgreSQL error code and the constraint name, so we know
/// the intended rule fired and not some other one.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ApplicationCatalogConstraintTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Role_operation_links_a_role_and_an_operation_of_the_same_application()
    {
        var billing = await AddApplication("billing");
        var role = await AddRole(billing, "Accountant");
        var operation = await AddOperation(billing, "InvoiceRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = role.Id, OperationId = operation.Id });
        await _db.SaveChangesAsync();

        (await _db.RoleOperations.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Role_operation_cannot_use_a_role_from_another_application()
    {
        var billing = await AddApplication("billing");
        var hr = await AddApplication("hr");
        var hrRole = await AddRole(hr, "Payroll");
        var billingOperation = await AddOperation(billing, "InvoiceRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = hrRole.Id, OperationId = billingOperation.Id });

        await ShouldViolate(PostgresErrorCodes.ForeignKeyViolation, "fk_role_operations_role_same_application");
    }

    [Fact]
    public async Task Role_operation_cannot_use_an_operation_from_another_application()
    {
        var billing = await AddApplication("billing");
        var hr = await AddApplication("hr");
        var billingRole = await AddRole(billing, "Accountant");
        var hrOperation = await AddOperation(hr, "SalaryRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = billingRole.Id, OperationId = hrOperation.Id });

        await ShouldViolate(PostgresErrorCodes.ForeignKeyViolation, "fk_role_operations_operation_same_application");
    }

    [Fact]
    public async Task Operation_name_must_start_with_its_application_key()
    {
        var billing = await AddApplication("billing");

        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = billing.Key, Name = "hr.SalaryRead" });

        await ShouldViolate(PostgresErrorCodes.CheckViolation, "ck_operations_name_has_application_prefix");
    }

    [Theory]
    [InlineData("billing.")]             // nothing after the prefix
    [InlineData("billing.a.b")]          // must be a single enum member name
    [InlineData("billing.Invoice Read")] // not a valid C# identifier
    [InlineData("billing.1Invoice")]     // identifiers can't start with a digit
    public async Task Operation_name_must_be_the_key_plus_an_enum_member_name(string name)
    {
        var billing = await AddApplication("billing");

        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = billing.Key, Name = name });

        await ShouldViolate(PostgresErrorCodes.CheckViolation, "ck_operations_name_format");
    }

    [Fact]
    public async Task Operation_must_use_its_own_application_key()
    {
        var billing = await AddApplication("billing");
        await AddApplication("hr");

        // Name and key agree with each other, but the key belongs to a different application.
        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = "hr", Name = "hr.SalaryRead" });

        await ShouldViolate(PostgresErrorCodes.ForeignKeyViolation, "fk_operations_application_id_key");
    }

    [Fact]
    public async Task Operation_name_is_unique_within_an_application()
    {
        var billing = await AddApplication("billing");
        await AddOperation(billing, "InvoiceRead");

        _db.Operations.Add(NewOperation(billing, "InvoiceRead"));

        await ShouldViolate(PostgresErrorCodes.UniqueViolation, "ix_operations_application_id_name");
    }

    [Fact]
    public async Task Operation_implication_cannot_start_from_an_operation_of_another_application()
    {
        var billing = await AddApplication("billing");
        var hr = await AddApplication("hr");
        var salaryManage = await AddOperation(hr, "SalaryManage");
        var invoiceRead = await AddOperation(billing, "InvoiceRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = salaryManage.Id, ImpliedOperationId = invoiceRead.Id,
        });

        await ShouldViolate(PostgresErrorCodes.ForeignKeyViolation, "fk_operation_implications_operation_same_application");
    }

    [Fact]
    public async Task Operation_implication_cannot_imply_an_operation_of_another_application()
    {
        var billing = await AddApplication("billing");
        var hr = await AddApplication("hr");
        var invoiceManage = await AddOperation(billing, "InvoiceManage");
        var salaryRead = await AddOperation(hr, "SalaryRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = invoiceManage.Id, ImpliedOperationId = salaryRead.Id,
        });

        await ShouldViolate(PostgresErrorCodes.ForeignKeyViolation, "fk_operation_implications_implied_same_application");
    }

    [Fact]
    public async Task Operation_cannot_imply_itself()
    {
        var billing = await AddApplication("billing");
        var invoiceRead = await AddOperation(billing, "InvoiceRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = invoiceRead.Id, ImpliedOperationId = invoiceRead.Id,
        });

        await ShouldViolate(PostgresErrorCodes.CheckViolation, "ck_operation_implications_not_self");
    }

    [Fact]
    public async Task Role_name_is_unique_within_an_application()
    {
        var billing = await AddApplication("billing");
        await AddRole(billing, "Accountant");

        _db.Roles.Add(NewRole(billing, "Accountant"));

        await ShouldViolate(PostgresErrorCodes.UniqueViolation, "ix_roles_application_id_name");
    }

    [Fact]
    public async Task Same_role_name_can_exist_in_different_applications()
    {
        var billing = await AddApplication("billing");
        var hr = await AddApplication("hr");

        await AddRole(billing, "Viewer");
        await AddRole(hr, "Viewer");

        (await _db.Roles.CountAsync(r => r.Name == "Viewer")).ShouldBe(2);
    }

    [Fact]
    public async Task Application_key_is_unique()
    {
        await AddApplication("billing");

        _db.Applications.Add(NewApplication("billing"));

        await ShouldViolate(PostgresErrorCodes.UniqueViolation, "ix_applications_key");
    }

    [Theory]
    [InlineData("Billing")]   // uppercase
    [InlineData("1billing")]  // must start with a letter
    [InlineData("bill_ing")]  // underscore not allowed
    [InlineData("b")]         // too short
    [InlineData("bill.ing")]  // a dot would make operation names ambiguous
    public async Task Application_key_must_be_a_lowercase_slug(string key)
    {
        _db.Applications.Add(NewApplication(key));

        await ShouldViolate(PostgresErrorCodes.CheckViolation, "ck_applications_key_format");
    }

    [Fact]
    public async Task Application_key_cannot_change_once_operations_use_it()
    {
        var billing = await AddApplication("billing");
        await AddOperation(billing, "InvoiceRead");

        // EF Core refuses to change the key in code already (it's an alternate key), so go through SQL
        // to prove the database protects it too.
        var error = await Should.ThrowAsync<PostgresException>(() =>
            _db.Database.ExecuteSqlAsync($"UPDATE applications SET key = 'invoicing' WHERE id = {billing.Id}"));

        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.ShouldBe("fk_operations_application_id_key");
    }

    private async Task ShouldViolate(string sqlState, string constraintName)
    {
        var error = await Should.ThrowAsync<DbUpdateException>(() => _db.SaveChangesAsync());

        var postgresError = error.InnerException.ShouldBeOfType<PostgresException>();
        postgresError.SqlState.ShouldBe(sqlState);
        postgresError.ConstraintName.ShouldBe(constraintName);
    }

    private async Task<Application> AddApplication(string key)
    {
        var application = NewApplication(key);
        _db.Applications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    private async Task<Operation> AddOperation(Application application, string member)
    {
        var operation = NewOperation(application, member);
        _db.Operations.Add(operation);
        await _db.SaveChangesAsync();
        return operation;
    }

    private async Task<Role> AddRole(Application application, string name)
    {
        var role = NewRole(application, name);
        _db.Roles.Add(role);
        await _db.SaveChangesAsync();
        return role;
    }

    private static Application NewApplication(string key) => new() { Key = key, Name = key };

    private static Operation NewOperation(Application application, string member) => new()
    {
        ApplicationId = application.Id,
        ApplicationKey = application.Key,
        Name = $"{application.Key}.{member}",
    };

    private static Role NewRole(Application application, string name) => new() { ApplicationId = application.Id, Name = name };
}
