using Microsoft.AspNetCore.Authorization;

namespace Auth.Client;

/// <summary>
/// Requires one of the given operations (OR). Stack several attributes to require all of them (AND).
/// Works on controllers, minimal API handlers and gRPC service methods, since it's ASP.NET Core authorization.
/// <code>[Authorize(BillingOperations.InvoiceRead)]</code>
/// </summary>
/// <remarks>
/// Implements <see cref="IAuthorizeData"/> itself instead of deriving from ASP.NET Core's AuthorizeAttribute,
/// whose public Policy setter would let <c>[Authorize(Op, Policy = "")]</c> silently drop the operation check.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class AuthorizeAttribute : Attribute, IAuthorizeData
{
    /// <summary>Policies with this prefix are built by <see cref="OperationPolicyProvider"/>.</summary>
    public const string PolicyPrefix = "kale-operations:";

    private readonly string _policy;

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

        // e.g. (BillingOperations)99, or a [Flags] combination: not one operation.
        var undefined = operations.FirstOrDefault(operation => !Enum.IsDefined(OperationsEnum, operation));
        if (undefined is not null)
        {
            throw new ArgumentException($"{undefined} is not a member of {OperationsEnum.Name}.", nameof(operations));
        }

        Operations = operations;

        // The enum's full name is part of the policy so a same-named member of another enum can't pass as ours;
        // the application key is added when the policy is built (it's configuration).
        _policy = $"{PolicyPrefix}{OperationsEnum.FullName}:{string.Join('|', operations.Select(operation => operation.ToString()))}";
    }

    public IReadOnlyList<object> Operations { get; }

    /// <summary>The authorization policy this attribute stands for, e.g. to check it in code with IAuthorizationService.</summary>
    public string PolicyName => _policy;

    /// <summary>Checked against the configured operations enum.</summary>
    public Type OperationsEnum { get; }

    // Read-only for ASP.NET Core: setting it does nothing, so the operation check can't be replaced.
    string? IAuthorizeData.Policy
    {
        get => _policy;
        set { }
    }

    string? IAuthorizeData.Roles
    {
        get => null;
        set { }
    }

    string? IAuthorizeData.AuthenticationSchemes
    {
        get => null;
        set { }
    }
}
