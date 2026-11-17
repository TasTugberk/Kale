// A desktop app that signs its user in through Kale (Kale.Auth.Desktop) and calls its own API: SampleApp's /notes.
// Against the local stack (./scripts/dev-stack.sh, which turns on desktop sign-in for "sample"):
//
//     dotnet run --project samples/SampleDesktop
//
// Other addresses: dotnet run --project samples/SampleDesktop -- <kale-address> <sampleapp-address>
using Auth.Desktop;

var kale = new Uri(args.Length > 0 ? args[0] : "http://auth.kale.localhost:8080/");
var sampleApp = new Uri(args.Length > 1 ? args[1] : "http://localhost:8082/");

using var signIn = new KaleDesktopSignIn(new KaleDesktopOptions
{
    Authority = kale,
    ApplicationKey = "sample",

    // The local stack speaks plain http; never in production.
    AllowInsecureHttpDevOnly = kale.Scheme == Uri.UriSchemeHttp,
});

Console.WriteLine("Signing in: finish in the browser window that opens.");
try
{
    await signIn.SignInAsync();
}
catch (Exception exception) when (exception is KaleSignInException or TimeoutException)
{
    Console.WriteLine($"Sign-in failed: {exception.Message}");
    return 1;
}

Console.WriteLine($"Signed in as {signIn.User?.Name} ({signIn.User?.UserName}).");

// Every call to the application's own API carries the user's access token, renewed when needed.
using var api = new HttpClient(new KaleBearerHandler(signIn) { InnerHandler = new HttpClientHandler() }) { BaseAddress = sampleApp };
using (var response = await api.GetAsync(new Uri("notes", UriKind.Relative)))
{
    Console.WriteLine($"GET /notes: {(int)response.StatusCode} {response.StatusCode}");
    Console.WriteLine(await response.Content.ReadAsStringAsync());
}

Console.Write("Press Enter to sign out.");
Console.ReadLine();
await signIn.SignOutAsync();
Console.WriteLine("Signed out.");
return 0;
