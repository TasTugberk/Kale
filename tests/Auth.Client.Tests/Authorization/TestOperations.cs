using Auth.Client;

namespace Auth.Client.Tests.Authorization;

/// <summary>An app's operations enum, as a client app would declare it.</summary>
public enum BillingOperations
{
    InvoiceRead,
    [Implies(InvoiceRead)] InvoiceManage,
    PaymentRead,
    [Implies(InvoiceManage, PaymentRead)] Admin,
}

public enum OtherOperations
{
    Something,
    InvoiceRead,
}

// Deliberately broken: the analyzer warns about this too, but the catalog must refuse it at startup anyway.
#pragma warning disable CA1069
public enum DuplicateValues
{
    Read = 1,
    View = 1, // the same value under two names: "View" could never be told apart from "Read"
}
#pragma warning restore CA1069

public enum ImpliesForeignMember
{
    [Implies(OtherOperations.Something)] Read,
}
