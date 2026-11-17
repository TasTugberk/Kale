using System.Diagnostics;

namespace Auth.Desktop;

/// <summary>Opens an address of Kale's in a browser.</summary>
public interface IBrowser
{
    /// <summary>
    /// Opens <paramref name="address"/> and returns right away: it doesn't wait for the user (the sign-in waits for
    /// the browser to come back to the app's loopback address).
    /// </summary>
    Task OpenAsync(Uri address, CancellationToken cancellationToken);
}

/// <summary>
/// The user's default browser. Owner's decision: the system browser, never a browser window inside the app, and
/// never the app collecting the password: the user types it only into Kale's own page, where they can check the
/// address, and a sign-in they already have there is reused.
/// </summary>
public sealed class SystemBrowser : IBrowser
{
    public Task OpenAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        // UseShellExecute: the operating system opens the address with the default browser (on Windows the shell;
        // on macOS and Linux .NET runs open / xdg-open).
        using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
