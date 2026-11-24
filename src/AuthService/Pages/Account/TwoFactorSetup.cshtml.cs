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
public sealed class TwoFactorSetupModel(SignInManager<User> signIn, UserManager<User> users, UserTwoFactor twoFactor) : PageModel
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
        var user = await users.GetUserAsync(User);
        if (user is not { IsActive: true })
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
        var user = await users.GetUserAsync(User);
        if (user is not { IsActive: true })
        {
            return await SignOutAsync();
        }

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage("/Account/TwoFactor");
        }

        // Only a code from the app here: recovery codes don't exist yet.
        var code = TwoFactorCode.From(Code);
        RecoveryCodes = code.IsFromAuthenticatorApp ? await twoFactor.TurnOnAsync(user, code.Value, cancellationToken) : null;
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
            // The first visit: a new key. Identity renews the security stamp with it, so refresh this browser's
            // cookie, or the next sign-in to an application would ask for the password again.
            await users.ResetAuthenticatorKeyAsync(user);
            await signIn.RefreshSignInAsync(user);
            key = (await users.GetAuthenticatorKeyAsync(user))!;
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
