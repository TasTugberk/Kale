namespace AuthService;

/// <summary>
/// The stable codes of every <see cref="RefusedException"/>. The admin API sends them with each reason, and the
/// admin UI shows its own text for each (admin-ui/messages, section "Refusals"; RefusalCodesTests checks that
/// every code here has one in both languages). The English detail stays for other API users and the admin commands.
/// A code never changes meaning; a new rule gets a new code.
/// </summary>
public static class RefusalCodes
{
    // A request the admin UI never sends this way (a field missing from the JSON body). Values: field.
    public const string FieldRequired = "request.field-required";

    // Values: key.
    public const string ApplicationKeyInvalid = "application.key-invalid";
    public const string ApplicationKeyTaken = "application.key-taken";
    public const string ApplicationNameRequired = "application.name-required";

    // Values: max.
    public const string ApplicationNameTooLong = "application.name-too-long";
    public const string ApplicationAddressMissing = "application.address-missing";

    // Values: address.
    public const string ApplicationAddressHasFragment = "application.address-has-fragment";
    public const string ApplicationAddressNotHttps = "application.address-not-https";
    public const string AuthAddressesFixed = "application.auth-addresses-fixed";
    public const string AuthCannotBeDeactivated = "application.auth-cannot-be-deactivated";
    public const string AuthNoDesktopSignIn = "application.auth-no-desktop-sign-in";
    public const string AuthCannotBeDeleted = "application.auth-cannot-be-deleted";

    // Values: key.
    public const string ApplicationStillActive = "application.still-active";

    public const string RoleNameRequired = "role.name-required";

    // Values: max.
    public const string RoleNameTooLong = "role.name-too-long";
    public const string RoleDescriptionTooLong = "role.description-too-long";

    // Values: name.
    public const string RoleNameTaken = "role.name-taken";

    // Values: operations (comma-separated).
    public const string RoleOperationsUnknown = "role.operations-unknown";

    public const string GroupNameRequired = "group.name-required";

    // Values: max.
    public const string GroupNameTooLong = "group.name-too-long";
    public const string GroupDescriptionTooLong = "group.description-too-long";

    // Values: name.
    public const string GroupNameTaken = "group.name-taken";

    public const string LastAdmin = "admin.last-admin";

    public const string DisplayNameRequired = "user.display-name-required";

    // Values: max.
    public const string DisplayNameTooLong = "user.display-name-too-long";
    public const string UserNameTooLong = "user.user-name-too-long";

    // Values: userName.
    public const string UserNameInvalid = "user.user-name-invalid";
    public const string UserNameTaken = "user.user-name-taken";
    public const string CannotDeleteSelf = "user.cannot-delete-self";
    public const string TwoFactorAlreadyOn = "user.two-factor-already-on";
    public const string DefaultRoleNotInApplication = "access.default-role-not-in-application";

    public const string PasswordRequired = "password.required";

    // Values: min.
    public const string PasswordTooShort = "password.too-short";
    public const string PasswordNeedsSymbol = "password.needs-symbol";
    public const string PasswordNeedsDigit = "password.needs-digit";
    public const string PasswordNeedsLowercase = "password.needs-lowercase";
    public const string PasswordNeedsUppercase = "password.needs-uppercase";

    // Values: count.
    public const string PasswordNeedsUniqueChars = "password.needs-unique-chars";
}
