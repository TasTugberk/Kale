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
        var billing = await _db.AddApplication("billing");
        var role = await _db.AddRole(billing, "Accountant");
        var operation = await _db.AddOperation(billing, "InvoiceRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = role.Id, OperationId = operation.Id });
        await _db.SaveChangesAsync();

        (await _db.RoleOperations.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Role_operation_cannot_use_a_role_from_another_application()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var hrRole = await _db.AddRole(hr, "Payroll");
        var billingOperation = await _db.AddOperation(billing, "InvoiceRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = hrRole.Id, OperationId = billingOperation.Id });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_role_operations_role_same_application");
    }

    [Fact]
    public async Task Role_operation_cannot_use_an_operation_from_another_application()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var billingRole = await _db.AddRole(billing, "Accountant");
        var hrOperation = await _db.AddOperation(hr, "SalaryRead");

        _db.RoleOperations.Add(new RoleOperation { ApplicationId = billing.Id, RoleId = billingRole.Id, OperationId = hrOperation.Id });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_role_operations_operation_same_application");
    }

    [Fact]
    public async Task Operation_name_must_start_with_its_application_key()
    {
        var billing = await _db.AddApplication("billing");

        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = billing.Key, Name = "hr.SalaryRead" });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.CheckViolation, "ck_operations_name_has_application_prefix");
    }

    [Theory]
    [InlineData("billing.")]             // nothing after the prefix
    [InlineData("billing.a.b")]          // must be a single enum member name
    [InlineData("billing.Invoice Read")] // not a valid C# identifier
    [InlineData("billing.1Invoice")]     // identifiers can't start with a digit
    public async Task Operation_name_must_be_the_key_plus_an_enum_member_name(string name)
    {
        var billing = await _db.AddApplication("billing");

        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = billing.Key, Name = name });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.CheckViolation, "ck_operations_name_format");
    }

    [Fact]
    public async Task Operation_must_use_its_own_application_key()
    {
        var billing = await _db.AddApplication("billing");
        await _db.AddApplication("hr");

        // Name and key agree with each other, but the key belongs to a different application.
        _db.Operations.Add(new Operation { ApplicationId = billing.Id, ApplicationKey = "hr", Name = "hr.SalaryRead" });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_operations_application_id_key");
    }

    [Fact]
    public async Task Operation_name_is_unique_within_an_application()
    {
        var billing = await _db.AddApplication("billing");
        await _db.AddOperation(billing, "InvoiceRead");

        _db.Operations.Add(TestData.NewOperation(billing, "InvoiceRead"));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.UniqueViolation, "ix_operations_application_id_name");
    }

    [Fact]
    public async Task Operation_implication_cannot_start_from_an_operation_of_another_application()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var salaryManage = await _db.AddOperation(hr, "SalaryManage");
        var invoiceRead = await _db.AddOperation(billing, "InvoiceRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = salaryManage.Id, ImpliedOperationId = invoiceRead.Id,
        });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_operation_implications_operation_same_application");
    }

    [Fact]
    public async Task Operation_implication_cannot_imply_an_operation_of_another_application()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");
        var invoiceManage = await _db.AddOperation(billing, "InvoiceManage");
        var salaryRead = await _db.AddOperation(hr, "SalaryRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = invoiceManage.Id, ImpliedOperationId = salaryRead.Id,
        });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.ForeignKeyViolation, "fk_operation_implications_implied_same_application");
    }

    [Fact]
    public async Task Operation_cannot_imply_itself()
    {
        var billing = await _db.AddApplication("billing");
        var invoiceRead = await _db.AddOperation(billing, "InvoiceRead");

        _db.OperationImplications.Add(new OperationImplication
        {
            ApplicationId = billing.Id, OperationId = invoiceRead.Id, ImpliedOperationId = invoiceRead.Id,
        });

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.CheckViolation, "ck_operation_implications_not_self");
    }

    [Fact]
    public async Task Role_name_is_unique_within_an_application()
    {
        var billing = await _db.AddApplication("billing");
        await _db.AddRole(billing, "Accountant");

        _db.Roles.Add(TestData.NewRole(billing, "Accountant"));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.UniqueViolation, "ix_roles_application_id_name");
    }

    [Fact]
    public async Task Same_role_name_can_exist_in_different_applications()
    {
        var billing = await _db.AddApplication("billing");
        var hr = await _db.AddApplication("hr");

        await _db.AddRole(billing, "Viewer");
        await _db.AddRole(hr, "Viewer");

        (await _db.Roles.CountAsync(r => r.Name == "Viewer")).ShouldBe(2);
    }

    [Fact]
    public async Task Application_key_is_unique()
    {
        await _db.AddApplication("billing");

        _db.Applications.Add(TestData.NewApplication("billing"));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.UniqueViolation, "ix_applications_key");
    }

    [Theory]
    [InlineData("Billing")]   // uppercase
    [InlineData("1billing")]  // must start with a letter
    [InlineData("bill_ing")]  // underscore not allowed
    [InlineData("b")]         // too short
    [InlineData("bill.ing")]  // a dot would make operation names ambiguous
    public async Task Application_key_must_be_a_lowercase_slug(string key)
    {
        _db.Applications.Add(TestData.NewApplication(key));

        await DbAssert.ShouldViolate(_db, PostgresErrorCodes.CheckViolation, "ck_applications_key_format");
    }

    [Fact]
    public async Task Application_key_cannot_change_once_operations_use_it()
    {
        var billing = await _db.AddApplication("billing");
        await _db.AddOperation(billing, "InvoiceRead");

        // EF Core refuses to change the key in code already (it's an alternate key), so go through SQL
        // to prove the database protects it too.
        var error = await Should.ThrowAsync<PostgresException>(() =>
            _db.Database.ExecuteSqlAsync($"UPDATE applications SET key = 'invoicing' WHERE id = {billing.Id}"));

        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.ShouldBe("fk_operations_application_id_key");
    }
}
