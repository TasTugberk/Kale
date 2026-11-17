using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AuthService.Domain;
using AuthService.SignIn;
using AuthService.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Pages.Account;

/// <summary>
/// A signed-in user changes their own password. Their open sessions in applications stop (a stolen session must
/// not outlive the password it came from); this browser stays signed in to Kale.
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class ChangePasswordModel(SignInManager<User> signIn, UserManager<User> users, UserPasswords passwords) : PageModel
{
    [BindProperty]
    public string CurrentPassword { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string RepeatedPassword { get; set; } = "";

    /// <summary>Set after the redirect that follows a change, so reloading the page doesn't send the form again.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Changed { get; set; }

    /// <summary>The resource name of the text that says why the change was refused; null when it wasn't.</summary>
    public string? Problem { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // Disabled (or gone) since signing in: nothing to change. Signing out sends them to the login page, which
        // refuses them.
        if (user is not { IsActive: true })
        {
            await signIn.SignOutAsync();
            return Redirect(Request.PathBase + SignInSetup.LoginPath);
        }

        Problem = await ChangeAsync(user, cancellationToken);
        if (Problem is not null)
        {
            return Page();
        }

        // A new cookie with the new security stamp: without it, Identity's next stamp check would sign this browser
        // out too. UserPasswords updated this same tracked user (one DbContext per request), so it has the new stamp.
        // The cookie keeps its expiry: still at most one session lifetime after the login.
        await signIn.RefreshSignInAsync(user);
        return RedirectToPage(new { changed = true });
    }

    private async Task<string?> ChangeAsync(User user, CancellationToken cancellationToken)
    {
        if (NewPassword != RepeatedPassword)
        {
            return "PasswordsDiffer";
        }

        // Checked like a login: a wrong current password counts toward the lockout, so this page can't be used to
        // guess it with a stolen cookie.
        var check = await signIn.CheckPasswordSignInAsync(user, CurrentPassword, lockoutOnFailure: true);
        if (check.IsLockedOut)
        {
            return "LockedOut";
        }

        if (!check.Succeeded)
        {
            return "WrongCurrentPassword";
        }

        try
        {
            await passwords.SetAsync(user.Id, NewPassword, cancellationToken);
            return null;
        }
        catch (ValidationException)
        {
            // Identity's reasons are in English only; the page shows its own text with all the rules instead.
            return "PasswordRulesNotMet";
        }
    }
}
