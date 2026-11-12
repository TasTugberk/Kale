namespace AuthService.Persistence;

/// <summary>
/// The first number of every PostgreSQL advisory lock AuthService takes (<c>pg_advisory_xact_lock(number, ...)</c>).
/// They live here together so a clash is visible: two uses sharing a number would wait for each other for no
/// reason. Give a new lock its own number here (a test checks they all differ).
/// </summary>
public static class AdvisoryLocks
{
    /// <summary>Registrations of one application's operations take turns (OperationRegistrar).</summary>
    public const int OperationRegistration = 1001;

    /// <summary>Instances creating or deleting server keys take turns (KeyRing).</summary>
    public const int KeyRotation = 1002;

    /// <summary>Changes that could remove the last admin take turns (LastAdminCheck).</summary>
    public const int LastAdmin = 1003;
}
