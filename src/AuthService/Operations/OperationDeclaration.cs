namespace AuthService.Operations;

/// <summary>
/// One member of an app's operations enum, as the app sends it on startup: the member name (no prefix) and
/// the member names it implies.
/// </summary>
public sealed record OperationDeclaration(string Name, IReadOnlyList<string> Implies);

/// <summary>Qualified names ("billing.InvoiceRead") per outcome, mirroring RegisterOperationsResponse.</summary>
public sealed record RegistrationResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Reactivated,
    IReadOnlyList<string> Obsoleted,
    int Unchanged);

/// <summary>The request itself is wrong (gRPC INVALID_ARGUMENT). Nothing was changed.</summary>
public sealed class InvalidRegistrationException : Exception
{
    public InvalidRegistrationException()
    {
    }

    public InvalidRegistrationException(string message)
        : base(message)
    {
    }

    public InvalidRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
