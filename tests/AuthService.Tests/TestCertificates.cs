using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AuthService.Tests;

internal static class TestCertificates
{
    /// <summary>Writes a throwaway self-signed certificate and its private key as PEM files, like Podman secrets.</summary>
    public static (string CertificatePath, string PrivateKeyPath) WriteSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=kale-data-protection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var directory = Directory.CreateTempSubdirectory("kale-dp-test-").FullName;
        var certificatePath = Path.Combine(directory, "cert.pem");
        var privateKeyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certificatePath, privateKeyPath);
    }
}
