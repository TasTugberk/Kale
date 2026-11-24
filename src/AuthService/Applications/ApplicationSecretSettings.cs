namespace AuthService.Applications;

/// <summary>
/// How long an application's old client secret keeps working after a rotation (Applications:OldSecretValidFor,
/// default 7 days): time to redeploy every instance with the new one.
/// </summary>
public sealed record ApplicationSecretSettings(TimeSpan OldSecretValidFor)
{
    public static readonly TimeSpan DefaultOldSecretValidFor = TimeSpan.FromDays(7);

    public static ApplicationSecretSettings From(IConfiguration configuration)
    {
        var validFor = configuration.GetValue("Applications:OldSecretValidFor", DefaultOldSecretValidFor);
        if (validFor <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"Applications:OldSecretValidFor must be positive, not {validFor}. To stop an old secret at once, retire it.");
        }

        return new ApplicationSecretSettings(validFor);
    }
}
