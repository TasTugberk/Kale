using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AuthService.Tests;

/// <summary>A throwaway self-signed certificate and its private key as PEM files, like Podman secrets.</summary>
internal sealed class TestCertificate : IDisposable
{
    private readonly string _directory;

    private TestCertificate(string directory)
    {
        _directory = directory;
    }

    public string CertificatePath => Path.Combine(_directory, "cert.pem");

    public string PrivateKeyPath => Path.Combine(_directory, "key.pem");

    public static TestCertificate Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=kale-data-protection-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var created = new TestCertificate(Directory.CreateTempSubdirectory("kale-dp-test-").FullName);
        File.WriteAllText(created.CertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(created.PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        return created;
    }

    /// <summary>Deletes the files, so no private key is left behind in the temp folder.</summary>
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal static class TestCertificates
{
    public static TestCertificate WriteSelfSigned() => TestCertificate.Create();
}
