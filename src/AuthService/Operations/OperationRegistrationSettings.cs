namespace AuthService.Operations;

/// <summary>
/// How long an operation stays active after the last registration that listed it (Operations:ObsoleteAfter).
/// </summary>
/// <remarks>
/// A grace period instead of "whatever the last registration left out is obsolete": during a rolling deployment,
/// a crash-restart or a rollback, two versions of an app run side by side and take turns registering, and each
/// would otherwise obsolete the other's new operations. It must be well above the client's re-registration
/// interval (5 minutes in Auth.Client), so a running version's operations never lapse between two registrations.
/// </remarks>
public sealed record OperationRegistrationSettings(TimeSpan ObsoleteAfter)
{
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(30);
}
