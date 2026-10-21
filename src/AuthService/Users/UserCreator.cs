using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using Microsoft.AspNetCore.Identity;

namespace AuthService.Users;

/// <summary>
/// Creates users: Identity's own checks (a free, valid user name and a strong enough password) plus a display
/// name. Every problem is reported in one <see cref="ValidationException"/>, before anything is written.
/// </summary>
public sealed class UserCreator(UserManager<User> users)
{
    // Same limit as the column, checked first for a readable error.
    private const int MaxDisplayNameLength = 200;

    /// <summary>The new user, not saved yet; throws when it couldn't be created.</summary>
    public async Task<User> CheckAsync(string userName, string displayName, string password)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            errors.Add("A display name is required.");
        }
        else if (displayName.Trim().Length > MaxDisplayNameLength)
        {
            errors.Add($"A display name can be at most {MaxDisplayNameLength} characters.");
        }

        var user = new User { UserName = userName, DisplayName = displayName?.Trim() ?? "" };
        foreach (var validator in users.UserValidators)
        {
            errors.AddRange((await validator.ValidateAsync(users, user)).Errors.Select(e => e.Description));
        }

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

    public async Task<User> CreateAsync(string userName, string displayName, string password)
    {
        var user = await CheckAsync(userName, displayName, password);
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        return user;
    }
}
