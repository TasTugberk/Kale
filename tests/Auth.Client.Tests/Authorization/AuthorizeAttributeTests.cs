namespace Auth.Client.Tests.Authorization;

public sealed class AuthorizeAttributeTests
{
    [Fact]
    public void Several_operations_in_one_attribute_form_one_policy_meaning_any_of_them()
    {
        var attribute = new AuthorizeAttribute(BillingOperations.InvoiceRead, BillingOperations.PaymentRead);

        ((Microsoft.AspNetCore.Authorization.IAuthorizeData)attribute).Policy.ShouldBe("kale-operations:BillingOperations:InvoiceRead|PaymentRead");
        attribute.OperationsEnum.ShouldBe(typeof(BillingOperations));
    }

    [Fact]
    public void Operations_must_be_enum_values()
    {
        Should.Throw<ArgumentException>(() => new AuthorizeAttribute("InvoiceRead"));
    }

    [Fact]
    public void Operations_must_come_from_one_enum()
    {
        Should.Throw<ArgumentException>(() => new AuthorizeAttribute(BillingOperations.InvoiceRead, OtherOperations.Something));
    }

    [Fact]
    public void The_policy_cannot_be_replaced_from_the_attribute()
    {
        // Otherwise [Authorize(Op, Policy = "")] would silently drop the operation check.
        typeof(AuthorizeAttribute).GetProperty("Policy").ShouldBeNull("no public Policy to set in [Authorize(...)]");

#pragma warning disable CA1859 // On purpose: ASP.NET Core only ever sees the attribute through IAuthorizeData.
        Microsoft.AspNetCore.Authorization.IAuthorizeData data = new AuthorizeAttribute(BillingOperations.InvoiceRead);
#pragma warning restore CA1859
        data.Policy = "something-else";
        data.Policy.ShouldBe("kale-operations:BillingOperations:InvoiceRead");
        data.Roles.ShouldBeNull();
        data.AuthenticationSchemes.ShouldBeNull();
    }

    [Fact]
    public void Values_that_are_not_members_are_refused()
    {
        Should.Throw<ArgumentException>(() => new AuthorizeAttribute((BillingOperations)99));
    }

    [Fact]
    public void At_least_one_operation_is_needed()
    {
        Should.Throw<ArgumentException>(() => new AuthorizeAttribute());
    }
}
