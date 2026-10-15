using AuthService.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuthService.Pages.Account;

public sealed class LoginModel(SignInManager<User> signIn, UserManager<User> users) : PageModel
{
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

        // Same message for every failure, so the page doesn't reveal which user names exist.
        Failed = true;
        Password = "";
        return Page();
    }
}
