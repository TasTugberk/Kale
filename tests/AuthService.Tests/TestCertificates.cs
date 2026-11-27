using System.Net.Security;
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

    /// <summary>
    /// A self-signed TLS server certificate for localhost (DNS name and both loopback addresses), standing for a
    /// certificate from an internal CA: clients trust it by adding it as their own root.
    /// </summary>
    public static TestCertificate CreateForLocalhost()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        names.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var created = new TestCertificate(Directory.CreateTempSubdirectory("kale-tls-test-").FullName);
        File.WriteAllText(created.CertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(created.PrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
        return created;
    }

    /// <summary>
    /// A handler that trusts this certificate as its only root, the way a client trusts an internal CA. Every other
    /// check stays: a certificate for another name is refused as usual. Not redirecting, and keeping cookies, like a
    /// browser.
    /// </summary>
    public HttpClientHandler TrustingHandler()
    {
        var root = X509Certificate2.CreateFromPem(File.ReadAllText(CertificatePath));
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => IsTrusted(certificate, errors, root),
        };
    }

    /// <summary>
    /// The handler's decision: the platform's own checks must pass except the chain, which must end at
    /// <paramref name="root"/> instead of the machine's trusted roots.
    /// </summary>
    internal static bool IsTrusted(X509Certificate2? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        // A self-signed test root has no revocation list to ask.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    /// <summary>Deletes the files, so no private key is left behind in the temp folder.</summary>
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

internal static class TestCertificates
{
    public static TestCertificate WriteSelfSigned() => TestCertificate.Create();
}
