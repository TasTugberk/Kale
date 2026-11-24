using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Users;

/// <summary>
/// Creates users: Identity's own checks (a free, valid user name and a strong enough password) plus a display
/// name. Every problem is reported in one <see cref="ValidationException"/>, before anything is written.
/// </summary>
public sealed class UserCreator(UserManager<User> users)
{
    // Same limits as the columns, checked first for a readable error (Identity doesn't check the length).
    private const int MaxDisplayNameLength = 200;
    private const int MaxUserNameLength = 256;

    /// <summary>The new user, not saved yet; throws when it couldn't be created.</summary>
    public async Task<User> CheckAsync(string userName, string displayName, string password)
    {
        var user = new User { UserName = userName, DisplayName = displayName?.Trim() ?? "" };
        var errors = await NameProblemsAsync(users, user);

        if (string.IsNullOrEmpty(password))
        {
            errors.Add("Give a password.");
        }
        else
        {
            foreach (var validator in users.PasswordValidators)
            {
                errors.AddRange((await validator.ValidateAsync(users, user, password)).Errors.Select(e => e.Description));
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(" ", errors));
        }

        return user;
    }

    /// <summary>
    /// What's wrong with the user's names (nothing: an empty list): a display name (already trimmed), and
    /// Identity's checks of the user name (allowed characters, not taken by another user). Also used by
    /// <see cref="UserEditor"/>, so a correction meets the same rules as a new user.
    /// </summary>
    public static async Task<List<string>> NameProblemsAsync(UserManager<User> users, User user)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(user.DisplayName))
        {
            errors.Add("A display name is required.");
        }
        else if (user.DisplayName.Length > MaxDisplayNameLength)
        {
            errors.Add($"A display name can be at most {MaxDisplayNameLength} characters.");
        }

        if (user.UserName?.Length > MaxUserNameLength)
        {
            errors.Add($"A user name can be at most {MaxUserNameLength} characters.");
        }

        foreach (var validator in users.UserValidators)
        {
            errors.AddRange((await validator.ValidateAsync(users, user)).Errors.Select(e => e.Description));
        }

        return errors;
    }

    public async Task<User> CreateAsync(string userName, string displayName, string password)
    {
        var user = await CheckAsync(userName, displayName, password);
        IdentityResult result;
        try
        {
            result = await users.CreateAsync(user, password);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two creates of the same name at once (e.g. a double submit) can both pass the check above; the
            // unique index stops the second, which gets the same answer as if it had come later.
            throw new ValidationException($"Username '{userName}' is already taken.", exception);
        }

        if (!result.Succeeded)
        {
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        return user;
    }
}
