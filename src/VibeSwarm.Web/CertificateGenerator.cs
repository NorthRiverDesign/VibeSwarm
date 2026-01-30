using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VibeSwarm.Web;

/// <summary>
/// Generates self-signed certificates for development and local deployments
/// </summary>
public static class CertificateGenerator
{
    /// <summary>
    /// Generates a self-signed certificate for the specified subject name.
    /// SANs include loopback addresses, the machine hostname, and all local
    /// network interface IPs so devices on the LAN can connect with a trusted cert.
    /// </summary>
    public static X509Certificate2 GenerateSelfSignedCertificate(string subjectName)
    {
        var distinguishedName = new X500DistinguishedName($"CN={subjectName}");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(distinguishedName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Basic constraints (end-entity, not a CA) — iOS is stricter about this
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

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

        // Build SANs: loopback + machine name + all local network interface IPs
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);

        // Dynamically add all non-loopback unicast IPs from active network interfaces
        foreach (var ip in GetLocalIpAddresses())
        {
            san.AddIpAddress(ip);
        }

        request.CertificateExtensions.Add(san.Build());

        // Create certificate valid for 10 years
        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        return certificate;
    }

    private static IEnumerable<IPAddress> GetLocalIpAddresses()
    {
        var addresses = new HashSet<string>();

        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;

                // Skip loopback (already added) and link-local addresses
                if (IPAddress.IsLoopback(ip))
                    continue;
                if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv6LinkLocal)
                    continue;

                // Deduplicate by string representation
                if (addresses.Add(ip.ToString()))
                    yield return ip;
            }
        }
    }
}
