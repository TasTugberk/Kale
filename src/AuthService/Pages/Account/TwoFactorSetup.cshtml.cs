using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.SignIn;
using AuthService.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Pages.Account;

/// <summary>
/// A signed-in user turns two-step sign-in on: the page shows a key (QR code and text) for their authenticator app,
/// a code from the app confirms it, and the recovery codes are shown once. With SignIn:RequireTwoFactor the authorize
/// endpoint sends users here (with the request as ReturnUrl) before it issues a code.
/// </summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class TwoFactorSetupModel(SignInManager<User> signIn, UserManager<User> users, IUserStore<User> store, UserTwoFactor twoFactor)
    : PageModel
{
    /// <summary>The authorize request to go back to, when the sign-in sent the user here.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Code { get; set; } = "";

    public string Key { get; private set; } = "";

    public string AuthenticatorUri { get; private set; } = "";

    public string QrCodeSvg { get; private set; } = "";

    /// <summary>Set once it's turned on: the codes to write down, on this response only.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public string? Problem { get; private set; }

    /// <summary>Where "continue" goes after the recovery codes: back to the sign-in, or the status page.</summary>
    public string ContinueUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : Url.Content("~/account/two-factor");

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

        // Already on: a new key here would break the one in the user's app.
        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/TwoFactor");
        }

        await ShowKeyAsync(user);
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

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/TwoFactor");
        }

        // Only a code from the app here: recovery codes don't exist yet.
        var code = TwoFactorCode.From(Code);
        try
        {
            RecoveryCodes = code.IsFromAuthenticatorApp ? await twoFactor.TurnOnAsync(user, code.Value, cancellationToken) : null;
        }
        catch (ValidationException)
        {
            // Turned on by another request meanwhile (a double submit): its recovery codes are the ones that count.
            return RedirectToPage("/Account/TwoFactor");
        }

        if (RecoveryCodes is null)
        {
            Problem = "WrongCode";
            await ShowKeyAsync(user);
            return Page();
        }

        // Turning it on renewed the security stamp, which signs out the user's other browsers; this one gets a cookie
        // with the new stamp. It keeps the sign-in time and methods: the user didn't sign in again.
        await signIn.RefreshSignInAsync(user);
        return Page();
    }

    private async Task ShowKeyAsync(User user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            // The first visit: a new key. Not ResetAuthenticatorKeyAsync, which also renews the security stamp: a plain
            // link to this page (from any site) would then sign the user out of their other browsers. An unconfirmed
            // key changes nothing yet; turning it on renews the stamp.
            key = users.GenerateNewAuthenticatorKey();
            await ((IUserAuthenticatorKeyStore<User>)store).SetAuthenticatorKeyAsync(user, key, HttpContext.RequestAborted);
            var saved = await users.UpdateAsync(user);
            if (!saved.Succeeded)
            {
                throw new InvalidOperationException(string.Join(" ", saved.Errors.Select(e => e.Description)));
            }
        }

        Key = AuthenticatorSetup.Readable(key);
        AuthenticatorUri = AuthenticatorSetup.Uri(user.UserName!, key);
        QrCodeSvg = AuthenticatorSetup.QrCodeSvg(AuthenticatorUri);
    }

    // Disabled (or gone) since signing in: signing out sends them to the login page, which refuses them.
    private async Task<IActionResult> SignOutAsync()
    {
        await signIn.SignOutAsync();
        return Redirect(Request.PathBase + SignInSetup.LoginPath);
    }
}
