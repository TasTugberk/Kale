using AuthService.Domain;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Users;

/// <summary>
/// Identity's reasons (its validators of user names and passwords) as refusal reasons with our codes, so the admin
/// UI can show them in the admin's language. Identity's error codes are its own and could change, so they're
/// translated here, in one place. The values come from the user and Identity's options (Identity puts them only
/// into its English text). A reason we don't know keeps Identity's text, under "identity.{its code}".
/// </summary>
public static class IdentityRefusals
{
    public static List<RefusalReason> ReasonsFor(IdentityResult result, UserManager<User> users, User user)
    {
        var reasons = new List<RefusalReason>();
        foreach (var error in result.Errors)
        {
            reasons.Add(ReasonFor(error, users.Options.Password, user));
        }

        return reasons;
    }

    private static RefusalReason ReasonFor(IdentityError error, PasswordOptions password, User user)
    {
        var userName = user.UserName ?? "";
        // Identity's codes are the names of IdentityErrorDescriber's methods.
        return error.Code switch
        {
            "DuplicateUserName" => new(RefusalCodes.UserNameTaken, error.Description, ("userName", userName)),
            "InvalidUserName" => new(RefusalCodes.UserNameInvalid, error.Description, ("userName", userName)),
            "PasswordTooShort" => new(RefusalCodes.PasswordTooShort, error.Description, ("min", $"{password.RequiredLength}")),
            "PasswordRequiresNonAlphanumeric" => new(RefusalCodes.PasswordNeedsSymbol, error.Description),
            "PasswordRequiresDigit" => new(RefusalCodes.PasswordNeedsDigit, error.Description),
            "PasswordRequiresLower" => new(RefusalCodes.PasswordNeedsLowercase, error.Description),
            "PasswordRequiresUpper" => new(RefusalCodes.PasswordNeedsUppercase, error.Description),
            "PasswordRequiresUniqueChars" => new(RefusalCodes.PasswordNeedsUniqueChars, error.Description, ("count", $"{password.RequiredUniqueChars}")),
            _ => new($"identity.{error.Code}", error.Description),
        };
    }
}
