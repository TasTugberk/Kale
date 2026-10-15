namespace Auth.Client.Tests.Authorization;

public sealed class AuthorizeAttributeTests
{
    [Fact]
    public void Several_operations_in_one_attribute_form_one_policy_meaning_any_of_them()
    {
        var attribute = new AuthorizeAttribute(BillingOperations.InvoiceRead, BillingOperations.PaymentRead);

        attribute.Policy.ShouldBe("kale-operations:InvoiceRead|PaymentRead");
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
    public void At_least_one_operation_is_needed()
    {
        Should.Throw<ArgumentException>(() => new AuthorizeAttribute());
    }
}
