using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VibeSwarm.Web;

/// <summary>
/// Generates self-signed certificates for development and local deployments
/// </summary>
public static class CertificateGenerator
{
    /// <summary>
    /// Generates a self-signed certificate for the specified subject name
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate(string subjectName)
    {
        var distinguishedName = new X500DistinguishedName($"CN={subjectName}");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Add key usage
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature,
                false));

        // Add enhanced key usage for server authentication
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, // Server Authentication
                false));

        // Add subject alternative names for localhost
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddIpAddress(System.Net.IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        // Create certificate valid for 10 years
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        return certificate;
    }
}
