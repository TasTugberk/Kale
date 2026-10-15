using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Pages.Account;

public sealed class LoginModel(SignInManager<User> signIn, UserManager<User> users) : PageModel
{
    // A hash of a random password, computed once. Checking a password against it costs what a real check
    // costs, so unknown and disabled users don't answer faster (which would reveal which user names exist).
    private static string? s_dummyPasswordHash;

    /// <summary>Normally the authorize request that sent the user here.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string UserName { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public bool Failed { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.FindByNameAsync(UserName);

        // Disabled users get no cookie at all; lockout counts wrong passwords.
        if (user is { IsActive: true })
        {
            var result = await signIn.PasswordSignInAsync(user, Password, isPersistent: false, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                // Only back into AuthService (the authorize request), never to another site.
                return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
            }
        }
        else
        {
            SpendAsLongAsAPasswordCheck();
        }

        // Same message for every failure, so the page doesn't reveal which user names exist.
        Failed = true;
        Password = "";
        return Page();
    }

    private void SpendAsLongAsAPasswordCheck()
    {
        var dummyUser = new User { UserName = "-", DisplayName = "-" };
        s_dummyPasswordHash ??= users.PasswordHasher.HashPassword(dummyUser, Guid.NewGuid().ToString());
        users.PasswordHasher.VerifyHashedPassword(dummyUser, s_dummyPasswordHash, Password);
    }
}
