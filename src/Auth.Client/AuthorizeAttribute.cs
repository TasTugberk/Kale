namespace Auth.Client;

/// <summary>
/// Requires one of the given operations (OR). Stack several attributes to require all of them (AND).
/// Works on controllers, minimal API handlers and gRPC service methods, since it's ASP.NET Core authorization.
/// <code>[Authorize(BillingOperations.InvoiceRead)]</code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class AuthorizeAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    /// <summary>Policies with this prefix are built by <see cref="OperationPolicyProvider"/>.</summary>
    public const string PolicyPrefix = "kale-operations:";

    public AuthorizeAttribute(params object[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Length == 0)
        {
            throw new ArgumentException("Name at least one operation.", nameof(operations));
        }

        if (operations.Any(operation => operation is not Enum))
        {
            throw new ArgumentException("Operations must be members of the application's operations enum.", nameof(operations));
        }

        OperationsEnum = operations[0].GetType();
        if (operations.Any(operation => operation.GetType() != OperationsEnum))
        {
            throw new ArgumentException("All operations in one attribute must come from the same enum.", nameof(operations));
        }

        Operations = operations;

        // Member names only: the application key is added when the policy is built (it's configuration).
        Policy = PolicyPrefix + string.Join('|', operations.Select(operation => operation.ToString()));
    }

    public IReadOnlyList<object> Operations { get; }

    /// <summary>Checked against the configured operations enum when the app starts.</summary>
    public Type OperationsEnum { get; }
}
