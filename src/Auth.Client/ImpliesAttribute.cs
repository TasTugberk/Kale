namespace Auth.Client;

/// <summary>
/// On an operations enum member: holding this operation also grants the listed ones, e.g.
/// <c>[Implies(InvoiceRead)] InvoiceManage</c>. AuthService expands the chain, so endpoints only declare the
/// lowest operation they need.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ImpliesAttribute(params object[] operations) : Attribute
{
    public IReadOnlyList<object> Operations { get; } = operations;
}
