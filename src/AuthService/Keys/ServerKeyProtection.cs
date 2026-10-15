using System.Security.Cryptography.X509Certificates;
using AuthService.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace AuthService.Keys;

/// <summary>
/// Data Protection encrypts the server keys. Its own key ring lives in the database (shared by all instances)
/// and, outside Development/Testing, is itself encrypted with a certificate loaded from files, e.g. Podman
/// secrets. Without that, anyone who can read the database could decrypt the signing keys.
/// </summary>
public static class ServerKeyProtection
{
    public const string ApplicationName = "Kale.AuthService";

    public static void AddServerKeyProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var dataProtection = services.AddDataProtection()
            .PersistKeysToDbContext<AuthDbContext>()
            .SetApplicationName(ApplicationName);

        var certificatePath = configuration["DataProtection:CertificatePath"];
        var privateKeyPath = configuration["DataProtection:PrivateKeyPath"];

        if (!string.IsNullOrWhiteSpace(certificatePath) && !string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);

            // UnprotectKeysWithAnyCertificate: on Linux, Data Protection can't find the certificate in a
            // certificate store when decrypting, so it has to be handed over explicitly.
            dataProtection
                .ProtectKeysWithCertificate(certificate)
                .UnprotectKeysWithAnyCertificate(certificate);
        }
        else if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Set DataProtection:CertificatePath and DataProtection:PrivateKeyPath (PEM files, e.g. Podman secrets). " +
                "Without them, Data Protection's keys, which encrypt the server's signing keys, would be stored unencrypted in the database.");
        }
    }
}
