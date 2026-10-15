using System.Security.Claims;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.SignIn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Pages.Account;

/// <summary>Lets a signed-in user with several eligible roles pick the one for this sign-in.</summary>
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class SelectRoleModel(AuthDbContext db, EligibleRoles eligibleRoles) : PageModel
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

        var clientId = QueryHelpers.ParseQuery(new Uri(new Uri("http://local"), ReturnUrl).Query)["client_id"].ToString();
        var applicationId = await db.Applications
            .Where(a => a.Key == clientId)
            .Select(a => (Guid?)a.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (applicationId is null)
        {
            return BadRequest();
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        Roles = await eligibleRoles.ForUserAsync(userId, applicationId.Value, cancellationToken);
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
        Url.IsLocalUrl(url) && url.StartsWith("/connect/authorize?", StringComparison.Ordinal);
}
