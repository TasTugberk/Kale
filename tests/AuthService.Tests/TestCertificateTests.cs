using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AuthService.Tests;

/// <summary>
/// The TLS tests' trusting handler only adds the test certificate as a root; it must not switch other checks off.
/// </summary>
public sealed class TestCertificateTests : IDisposable
{
    private readonly TestCertificate _trusted = TestCertificate.CreateForLocalhost();
    private readonly TestCertificate _other = TestCertificate.CreateForLocalhost();

    public void Dispose()
    {
        _trusted.Dispose();
        _other.Dispose();
    }

    [Fact]
    public void A_certificate_from_the_trusted_root_is_accepted_although_the_machine_does_not_know_that_root()
    {
        TestCertificate.IsTrusted(Load(_trusted), SslPolicyErrors.RemoteCertificateChainErrors, Load(_trusted)).ShouldBeTrue();
    }

    [Fact]
    public void A_certificate_for_another_name_is_refused()
    {
        TestCertificate.IsTrusted(
            Load(_trusted), SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors, Load(_trusted))
            .ShouldBeFalse();
    }

    [Fact]
    public void A_certificate_from_another_root_is_refused()
    {
        TestCertificate.IsTrusted(Load(_other), SslPolicyErrors.RemoteCertificateChainErrors, Load(_trusted)).ShouldBeFalse();
    }

    private static X509Certificate2 Load(TestCertificate certificate) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(certificate.CertificatePath));
}
