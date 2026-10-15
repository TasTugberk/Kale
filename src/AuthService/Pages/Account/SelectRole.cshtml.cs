using System.Security.Claims;
using AuthService.Roles;
using AuthService.SignIn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace AuthService.Pages.Account;

/// <summary>Lets a signed-in user with several eligible roles pick the one for this sign-in.</summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class SelectRoleModel(UserSignIn signIn) : PageModel
{
    /// <summary>The authorize request to continue; the choice is added to it.</summary>
    [BindProperty(SupportsGet = true)]
    public string ReturnUrl { get; set; } = "";

    [BindProperty]
    public Guid RoleId { get; set; }

    public IReadOnlyList<EligibleRole> Roles { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!IsAuthorizeRequest(ReturnUrl))
        {
            return BadRequest();
        }

        var query = QueryHelpers.ParseQuery(ReturnUrl[ReturnUrl.IndexOf('?', StringComparison.Ordinal)..]);
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var roles = await signIn.RolesToChooseFromAsync(userId, query["client_id"].ToString(), cancellationToken);
        if (roles is null)
        {
            return BadRequest();
        }

        Roles = roles;
        return Page();
    }

    public IActionResult OnPost()
    {
        if (!IsAuthorizeRequest(ReturnUrl))
        {
            return BadRequest();
        }

        // No eligibility check here: the authorize endpoint checks the choice against the user's roles.
        return LocalRedirect(QueryHelpers.AddQueryString(ReturnUrl, AuthorizeEndpoint.RoleParameter, RoleId.ToString()));
    }

    // Only ever continue a sign-in on this server, never redirect elsewhere.
    private bool IsAuthorizeRequest(string url) =>
        Url.IsLocalUrl(url) && url.StartsWith(Request.PathBase + AuthorizeEndpoint.Path + "?", StringComparison.Ordinal);
}
