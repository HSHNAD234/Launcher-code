
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HSHONAD_Launcher;

public static class ProjectCaManager
{
    public static void CreateRootCa(string path, string password)
    {
        if (File.Exists(path))
            throw new IOException(
                "Root CA already exists. Refusing to overwrite it.");

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);

        using RSA key = RSA.Create(3072);

        var request = new CertificateRequest(
            "CN=HSHONAD Project Root CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign, true));

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(
                request.PublicKey, false));

        using var ca = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));

        File.WriteAllBytes(
            path, ca.Export(X509ContentType.Pfx, password));
    }

    public static void CreateLocalhostCertificate(
        string caPath,
        string outputPath,
        string password)
    {
        using var ca = new X509Certificate2(
            caPath, password,
            X509KeyStorageFlags.Exportable |
            X509KeyStorageFlags.EphemeralKeySet);

        using RSA caKey = ca.GetRSAPrivateKey()
            ?? throw new CryptographicException(
                "Root CA private key is missing.");

        using RSA serverKey = RSA.Create(2048);

        var request = new CertificateRequest(
            "CN=localhost",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment, true));

        var eku = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(eku, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);

        request.CertificateExtensions.Add(san.Build());

        using var publicCert = request.Create(
            ca,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(90),
            RandomNumberGenerator.GetBytes(16));

        using var certWithKey =
            publicCert.CopyWithPrivateKey(serverKey);

        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)!);

        File.WriteAllBytes(
            outputPath,
            certWithKey.Export(X509ContentType.Pfx, password));
    }
}
