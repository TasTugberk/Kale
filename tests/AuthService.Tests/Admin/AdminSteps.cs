using System.Net;
using AuthService.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.SignIn;

namespace AuthService.Tests.Admin;

/// <summary>Setting up the admin side the way an operator does, and signing users in through the real flow.</summary>
internal static class AdminSteps
{
    public const string Password = "Correct-horse-9";

    /// <summary>`admin bootstrap` for the user "ayse"; returns the "auth" application's client secret.</summary>
    public static async Task<string> BootstrapAsync(AuthServiceApp app)
    {
        var result = await RunAsync(app, Password + "\n", "bootstrap", "ayse", "Ayşe Admin", Browser.RedirectUri(AuthApplication.Key).AbsoluteUri);
        result.ExitCode.ShouldBe(0, result.Errors);
        return result.Output.Trim();
    }

    public static async Task<(int ExitCode, string Output, string Errors)> RunAsync(AuthServiceApp app, string input, params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = await AdminCommands.RunAsync(app.Services, args, new StringReader(input), output, errors);
        return (exitCode, output.ToString(), errors.ToString());
    }

    /// <summary>Signs the user in to the application (code + PKCE) and returns the access token.</summary>
    public static async Task<string> SignInAsync(AuthServiceApp app, string userName, string clientId, string clientSecret)
    {
        using var browser = new Browser(app);
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri(clientId)));
        var redirect = await browser.FollowAsync(await browser.LogInAsync(loginPage, userName, Password));
        var (status, body) = await browser.ExchangeCodeAsync(clientId, clientSecret, Browser.CodeFrom(redirect));
        status.ShouldBe(HttpStatusCode.OK);
        return body.RootElement.GetProperty("access_token").GetString()!;
    }
}
