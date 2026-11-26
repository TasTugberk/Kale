namespace AuthService;

/// <summary>
/// The stable codes of every <see cref="RefusedException"/>. The admin API sends them with each reason, and the
/// admin UI shows its own text for each (admin-ui/messages, section "Refusals"; RefusalCodesTests checks that
/// every code here has one in both languages). The English detail stays for other API users and the admin commands.
/// A code never changes meaning; a new rule gets a new code. The comment after a code names the values its
/// reason carries (the admin UI's texts use them); no comment means none.
/// </summary>
public static class RefusalCodes
{
    // A request the admin UI never sends this way (a field missing from the JSON body).
    public const string FieldRequired = "request.field-required"; // values: field

    // Another change to the same rows at the same moment, and PostgreSQL ended this one (a deadlock or a
    // serialization failure): nothing was saved, and the same request again normally works.
    public const string ConcurrentChange = "request.concurrent-change";

    public const string ApplicationKeyInvalid = "application.key-invalid"; // values: key
    public const string ApplicationKeyTaken = "application.key-taken"; // values: key
    public const string ApplicationNameRequired = "application.name-required";
    public const string ApplicationNameTooLong = "application.name-too-long"; // values: max
    public const string ApplicationAddressMissing = "application.address-missing";
    public const string ApplicationAddressHasFragment = "application.address-has-fragment"; // values: address
    public const string ApplicationAddressNotHttps = "application.address-not-https"; // values: address
    public const string AuthAddressesFixed = "application.auth-addresses-fixed";
    public const string AuthCannotBeDeactivated = "application.auth-cannot-be-deactivated";
    public const string AuthNoDesktopSignIn = "application.auth-no-desktop-sign-in";
    public const string AuthCannotBeDeleted = "application.auth-cannot-be-deleted";
    public const string ApplicationStillActive = "application.still-active"; // values: key

    public const string RoleNameRequired = "role.name-required";
    public const string RoleNameTooLong = "role.name-too-long"; // values: max
    public const string RoleDescriptionTooLong = "role.description-too-long"; // values: max
    public const string RoleNameTaken = "role.name-taken"; // values: name
    public const string RoleOperationsUnknown = "role.operations-unknown"; // values: operations (comma-separated)

    public const string GroupNameRequired = "group.name-required";
    public const string GroupNameTooLong = "group.name-too-long"; // values: max
    public const string GroupDescriptionTooLong = "group.description-too-long"; // values: max
    public const string GroupNameTaken = "group.name-taken"; // values: name

    public const string LastAdmin = "admin.last-admin";

    public const string DisplayNameRequired = "user.display-name-required";
    public const string DisplayNameTooLong = "user.display-name-too-long"; // values: max
    public const string UserNameTooLong = "user.user-name-too-long"; // values: max
    public const string UserNameInvalid = "user.user-name-invalid"; // values: userName
    public const string UserNameTaken = "user.user-name-taken"; // values: userName
    public const string CannotDeleteSelf = "user.cannot-delete-self";
    public const string TwoFactorAlreadyOn = "user.two-factor-already-on";
    public const string DefaultRoleNotInApplication = "access.default-role-not-in-application";

    public const string PasswordRequired = "password.required";
    public const string PasswordTooShort = "password.too-short"; // values: min
    public const string PasswordNeedsSymbol = "password.needs-symbol";
    public const string PasswordNeedsDigit = "password.needs-digit";
    public const string PasswordNeedsLowercase = "password.needs-lowercase";
    public const string PasswordNeedsUppercase = "password.needs-uppercase";
    public const string PasswordNeedsUniqueChars = "password.needs-unique-chars"; // values: count
}
