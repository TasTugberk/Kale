namespace AuthService.Domain;

/// <summary>
/// "Holding <see cref="OperationId"/> also grants <see cref="ImpliedOperationId"/>", from [Implies(...)] on the enum.
/// Example: billing.InvoiceManage implies billing.InvoiceRead.
/// </summary>
public sealed class OperationImplication
{
    public Guid ApplicationId { get; init; }

    public Guid OperationId { get; init; }

    public Guid ImpliedOperationId { get; init; }
}
