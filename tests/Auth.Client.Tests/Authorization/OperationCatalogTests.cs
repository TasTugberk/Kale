namespace Auth.Client.Tests.Authorization;

public sealed class OperationCatalogTests
{
    [Fact]
    public void Every_enum_member_becomes_an_operation_with_its_implications()
    {
        var catalog = OperationCatalog.For<BillingOperations>("billing");

        catalog.Declarations.ShouldBe(
        [
            new OperationDeclaration("InvoiceRead", []),
            new OperationDeclaration("InvoiceManage", ["InvoiceRead"]),
            new OperationDeclaration("PaymentRead", []),
            new OperationDeclaration("Admin", ["InvoiceManage", "PaymentRead"]),
        ], ignoreOrder: true, comparer: new DeclarationComparer());
    }

    [Fact]
    public void Operation_names_carry_the_application_key()
    {
        var catalog = OperationCatalog.For<BillingOperations>("billing");

        catalog.QualifiedName("InvoiceRead").ShouldBe("billing.InvoiceRead");
    }

    [Fact]
    public void An_unknown_member_is_a_programming_error()
    {
        var catalog = OperationCatalog.For<BillingOperations>("billing");

        Should.Throw<InvalidOperationException>(() => catalog.QualifiedName("Missing"));
    }

    [Fact]
    public void Two_members_with_the_same_value_are_refused_at_startup()
    {
        var error = Should.Throw<InvalidOperationException>(() => OperationCatalog.For<DuplicateValues>("billing"));

        error.Message.ShouldContain("View");
    }

    [Fact]
    public void Implying_a_member_of_another_enum_is_refused_at_startup()
    {
        Should.Throw<InvalidOperationException>(() => OperationCatalog.For<ImpliesForeignMember>("billing"));
    }

    private sealed class DeclarationComparer : IEqualityComparer<OperationDeclaration>
    {
        public bool Equals(OperationDeclaration? x, OperationDeclaration? y) =>
            x!.Name == y!.Name && x.Implies.Order().SequenceEqual(y.Implies.Order());

        public int GetHashCode(OperationDeclaration obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }
}
