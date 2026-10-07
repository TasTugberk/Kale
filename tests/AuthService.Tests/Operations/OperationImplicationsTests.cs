using AuthService.Operations;

namespace AuthService.Tests.Operations;

public sealed class OperationImplicationsTests
{
    // ----- Expand: what a role effectively holds -----

    [Fact]
    public void Without_implications_a_role_holds_exactly_what_it_was_granted()
    {
        var effective = OperationImplications.Expand(["billing.InvoiceRead"], Implies());

        effective.ShouldBe(["billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public void A_granted_operation_adds_what_it_implies()
    {
        var effective = OperationImplications.Expand(
            ["billing.InvoiceManage"],
            Implies(("billing.InvoiceManage", "billing.InvoiceRead")));

        effective.ShouldBe(["billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public void Implications_are_followed_transitively()
    {
        var effective = OperationImplications.Expand(
            ["billing.Admin"],
            Implies(("billing.Admin", "billing.InvoiceManage"), ("billing.InvoiceManage", "billing.InvoiceRead")));

        effective.ShouldBe(["billing.Admin", "billing.InvoiceManage", "billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public void Implications_of_operations_the_role_does_not_hold_are_ignored()
    {
        var effective = OperationImplications.Expand(
            ["billing.InvoiceRead"],
            Implies(("billing.InvoiceManage", "billing.InvoiceRead"), ("billing.Admin", "billing.InvoiceManage")));

        effective.ShouldBe(["billing.InvoiceRead"], ignoreOrder: true);
    }

    [Fact]
    public void Operations_reached_through_several_paths_appear_once()
    {
        var effective = OperationImplications.Expand(
            ["billing.Admin"],
            Implies(
                ("billing.Admin", "billing.InvoiceManage"),
                ("billing.Admin", "billing.PaymentManage"),
                ("billing.InvoiceManage", "billing.Read"),
                ("billing.PaymentManage", "billing.Read")));

        effective.ShouldBe(
            ["billing.Admin", "billing.InvoiceManage", "billing.PaymentManage", "billing.Read"], ignoreOrder: true);
    }

    [Fact]
    public void A_cycle_does_not_loop_forever()
    {
        var effective = OperationImplications.Expand(
            ["billing.A"],
            Implies(("billing.A", "billing.B"), ("billing.B", "billing.A")));

        effective.ShouldBe(["billing.A", "billing.B"], ignoreOrder: true);
    }

    [Fact]
    public void A_role_with_no_operations_holds_nothing()
    {
        var effective = OperationImplications.Expand([], Implies(("billing.A", "billing.B")));

        effective.ShouldBeEmpty();
    }

    // ----- FindCycle: registration rejects enums whose [Implies] loop -----

    [Fact]
    public void No_cycle_is_found_in_a_chain()
    {
        OperationImplications.FindCycle(Implies(("A", "B"), ("B", "C"))).ShouldBeNull();
    }

    [Fact]
    public void A_diamond_is_not_a_cycle()
    {
        // Two paths to the same operation are fine; only a path back to the start is a cycle.
        var implies = Implies(("A", "B"), ("A", "C"), ("B", "D"), ("C", "D"));

        OperationImplications.FindCycle(implies).ShouldBeNull();
    }

    [Fact]
    public void A_two_operation_cycle_is_found_with_its_path()
    {
        var cycle = OperationImplications.FindCycle(Implies(("A", "B"), ("B", "A")));

        cycle.ShouldBe(["A", "B", "A"]);
    }

    [Fact]
    public void A_longer_cycle_is_found_with_its_path()
    {
        var cycle = OperationImplications.FindCycle(Implies(("A", "B"), ("B", "C"), ("C", "A"), ("C", "D")));

        cycle.ShouldBe(["A", "B", "C", "A"]);
    }

    [Fact]
    public void A_cycle_is_found_even_when_the_first_operation_is_not_part_of_it()
    {
        // X -> Y has no cycle, so the search has to move on to the next operation.
        var cycle = OperationImplications.FindCycle(Implies(("X", "Y"), ("A", "B"), ("B", "A")));

        cycle.ShouldBe(["A", "B", "A"]);
    }

    [Fact]
    public void An_operation_implying_itself_is_a_cycle()
    {
        OperationImplications.FindCycle(Implies(("A", "A"))).ShouldBe(["A", "A"]);
    }

    /// <summary>Builds the "operation -> operations it implies" lookup from (from, to) pairs.</summary>
    private static ILookup<string, string> Implies(params (string From, string To)[] pairs) =>
        pairs.ToLookup(pair => pair.From, pair => pair.To);
}
