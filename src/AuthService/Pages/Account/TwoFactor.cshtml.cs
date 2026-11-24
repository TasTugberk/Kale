using AuthService.Domain;
using AuthService.SignIn;
using AuthService.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Pages.Account;

/// <summary>
/// A signed-in user's two-step sign-in: whether it's on and how many recovery codes are left, with a link to set it
/// up, or a form to turn it off. Turning it off asks for a code (from the app or a recovery code), so a stolen
/// sign-in cookie alone can't remove the second factor; wrong codes count toward the lockout.
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class TwoFactorModel(SignInManager<User> signIn, UserManager<User> users, UserTwoFactor twoFactor) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = "";

    public bool IsOn { get; private set; }

    public int RecoveryCodesLeft { get; private set; }

    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // Like connect/authorize: a cookie from before a new security stamp (an admin's reset, a new password) is no
        // sign-in. Otherwise an old cookie could set up its own authenticator after a reset and take the account.
        var user = await signIn.ValidateSecurityStampAsync(User);
        if (user is null)
        {
            return Challenge(IdentityConstants.ApplicationScheme);
        }

        if (!user.IsActive)
        {
            return await SignOutAsync();
        }

        await ShowAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Like connect/authorize: a cookie from before a new security stamp (an admin's reset, a new password) is no
        // sign-in. Otherwise an old cookie could set up its own authenticator after a reset and take the account.
        var user = await signIn.ValidateSecurityStampAsync(User);
        if (user is null)
        {
            return Challenge(IdentityConstants.ApplicationScheme);
        }

        if (!user.IsActive)
        {
            return await SignOutAsync();
        }

        if (!user.TwoFactorEnabled)
        {
            return RedirectToPage();
        }

        Problem = await CheckCodeAsync(user);
        if (Problem is not null)
        {
            await ShowAsync(user);
            return Page();
        }

        await twoFactor.TurnOffAsync(user, cancellationToken);

        // A new security stamp came with it: keep this browser signed in.
        await signIn.RefreshSignInAsync(user);
        return RedirectToPage();
    }

    // Like a sign-in: a locked-out user can't try, and a wrong code counts.
    private async Task<string?> CheckCodeAsync(User user)
    {
        if (await users.IsLockedOutAsync(user))
        {
            return "LockedOut";
        }

        var code = TwoFactorCode.From(Code);
        var right = code.IsFromAuthenticatorApp
            ? await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code.Value)
            : (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Value)).Succeeded;
        if (right)
        {
            await users.ResetAccessFailedCountAsync(user);
            return null;
        }

        await users.AccessFailedAsync(user);
        return await users.IsLockedOutAsync(user) ? "LockedOut" : "WrongCode";
    }

    private async Task ShowAsync(User user)
    {
        IsOn = user.TwoFactorEnabled;
        RecoveryCodesLeft = IsOn ? await users.CountRecoveryCodesAsync(user) : 0;
        Code = "";
    }

    private async Task<IActionResult> SignOutAsync()
    {
        await signIn.SignOutAsync();
        return Redirect(Request.PathBase + SignInSetup.LoginPath);
    }
}
