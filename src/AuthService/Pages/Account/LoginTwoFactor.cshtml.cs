using AuthService.Domain;
using AuthService.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace AuthService.Pages.Account;

/// <summary>
/// The second step of signing in, after the password: a code from the authenticator app or a recovery code.
/// Identity's short-lived cookie (5 minutes) says who entered the password; a right code replaces it with the
/// sign-in cookie, whose sign-in time (max_age, auth_time) is now. No "remember this browser": every sign-in asks.
/// </summary>
public sealed class LoginTwoFactorModel(SignInManager<User> signIn, UserManager<User> users) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string Code { get; set; } = "";

    /// <summary>The resource name of the text that says why the code was refused; null when it wasn't.</summary>
    public string? Problem { get; private set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.GetTwoFactorAuthenticationUserAsync() is null ? BackToLogin() : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        // Nobody entered a password in this browser in the last 5 minutes.
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            return BackToLogin();
        }

        var code = TwoFactorCode.From(Code);
        var result = code.IsFromAuthenticatorApp
            ? await signIn.TwoFactorAuthenticatorSignInAsync(code.Value, isPersistent: false, rememberClient: false)
            : await signIn.TwoFactorRecoveryCodeSignInAsync(code.Value);

        if (result.Succeeded)
        {
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/account/signed-in");
        }

        // Identity counts a wrong authenticator code toward the lockout, but not a wrong recovery code: count it
        // here, or someone who knows the password could try recovery codes without limit.
        if (!code.IsFromAuthenticatorApp && !result.IsLockedOut)
        {
            await users.AccessFailedAsync(user);
        }

        Problem = result.IsLockedOut || await users.IsLockedOutAsync(user) ? "LockedOut" : "WrongCode";
        Code = "";
        return Page();
    }

    private RedirectResult BackToLogin()
    {
        var login = Request.PathBase + SignInSetup.LoginPath;
        return Redirect(Url.IsLocalUrl(ReturnUrl) ? QueryHelpers.AddQueryString(login, "ReturnUrl", ReturnUrl!) : login);
    }
}
