using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tdm.Core.Settings;

namespace Tdm.Observability.Audit;

public enum VerifyStatus { Verified, PartiallyVerified, Tampered, Error }

/// <summary>Result of <see cref="ManifestSigner.Verify"/>; <see cref="ExitCode"/> matches the
/// rest of the TDM CLI (0 success, 1 partial/warning, 2 failure).</summary>
public sealed record VerifyOutcome(VerifyStatus Status, string Message)
{
    public int ExitCode => Status switch
    {
        VerifyStatus.Verified => 0,
        VerifyStatus.PartiallyVerified => 1,
        _ => 2,
    };

    public static VerifyOutcome Verified(string message) => new(VerifyStatus.Verified, message);
    public static VerifyOutcome PartiallyVerified(string message) => new(VerifyStatus.PartiallyVerified, message);
    public static VerifyOutcome Tampered(string message) => new(VerifyStatus.Tampered, message);
    public static VerifyOutcome Error(string message) => new(VerifyStatus.Error, message);
}

/// <summary>
/// Manifest tamper-evidence (W2-D2): a SHA-256 checksum is always written next to the
/// manifest; an optional detached RSA signature over an X.509 private key adds real
/// tamper-evidence for orgs with PKI ("checksum-only mode" otherwise — see the risk table
/// in wave-2-handoff.md). <see cref="Verify"/> is the corresponding `tdm manifest verify` check.
/// </summary>
public static class ManifestSigner
{
    private const string ChecksumSuffix = ".sha256";
    private const string SignatureSuffix = ".sig";

    /// <summary>Writes "{hex}  {filename}" next to the manifest — sha256sum-compatible.</summary>
    public static string WriteChecksum(string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        var hash = ComputeHashHex(fullPath);
        var checksumPath = fullPath + ChecksumSuffix;
        File.WriteAllText(checksumPath, $"{hash}  {Path.GetFileName(fullPath)}\n");
        return checksumPath;
    }

    /// <summary>Resolves the certificate password from <see cref="SigningSettings.CertificatePasswordEnv"/>
    /// (the only environment read in this class) and delegates to <see cref="Sign"/>.</summary>
    public static string SignFromSettings(string manifestPath, SigningSettings signing)
    {
        var password = string.IsNullOrEmpty(signing.CertificatePasswordEnv)
            ? null
            : Environment.GetEnvironmentVariable(signing.CertificatePasswordEnv);
        return Sign(manifestPath, signing.CertificatePath, password);
    }

    /// <summary>Signs the manifest with the RSA private key in a PKCS#12 (.pfx) certificate.
    /// Writes a base64 detached signature to "{manifestPath}.sig".</summary>
    public static string Sign(string manifestPath, string certificatePath, string? password)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        // Ephemeral: the private key lives in memory only — default key storage tries to
        // persist it to a user key container, which fails in CI/container environments with
        // no writable profile (exactly where manifest signing is meant to run).
        using var cert = X509CertificateLoader.LoadPkcs12FromFile(
            Path.GetFullPath(certificatePath), password, X509KeyStorageFlags.EphemeralKeySet);
        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                $"Certificate '{certificatePath}' has no RSA private key — cannot sign the manifest.");

        var signature = rsa.SignData(File.ReadAllBytes(fullPath), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sigPath = fullPath + SignatureSuffix;
        File.WriteAllText(sigPath, Convert.ToBase64String(signature));
        return sigPath;
    }

    /// <summary>
    /// Checks the checksum (always required) and, if a signature file is present, verifies it
    /// against <paramref name="publicCertPath"/> when supplied. A signature file present
    /// without a certificate to check it against is reported as partial, not failed — the
    /// caller may only want the (weaker) checksum guarantee locally.
    /// </summary>
    public static VerifyOutcome Verify(string manifestPath, string? publicCertPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
            return VerifyOutcome.Error($"Manifest not found: {fullPath}");

        var checksumPath = fullPath + ChecksumSuffix;
        if (!File.Exists(checksumPath))
            return VerifyOutcome.Error($"No checksum file found: {checksumPath} — cannot verify integrity.");

        var recorded = ParseChecksumLine(File.ReadAllText(checksumPath));
        if (recorded is null)
            return VerifyOutcome.Error($"Checksum file '{checksumPath}' is not in the expected '<hex>  <filename>' format.");

        var actual = ComputeHashHex(fullPath);
        if (!string.Equals(actual, recorded, StringComparison.OrdinalIgnoreCase))
            return VerifyOutcome.Tampered($"Checksum mismatch for '{fullPath}' — the manifest has changed since it was written.");

        var sigPath = fullPath + SignatureSuffix;
        if (!File.Exists(sigPath))
            return VerifyOutcome.Verified("Checksum OK. No signature file present (checksum-only mode).");

        if (string.IsNullOrWhiteSpace(publicCertPath))
        {
            return VerifyOutcome.PartiallyVerified(
                "Checksum OK. A signature file is present but no --cert was supplied — signature not verified.");
        }

        try
        {
            using var cert = X509CertificateLoader.LoadCertificateFromFile(Path.GetFullPath(publicCertPath));
            using var rsa = cert.GetRSAPublicKey()
                ?? throw new InvalidOperationException($"Certificate '{publicCertPath}' has no RSA public key.");
            var signature = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
            var signatureOk = rsa.VerifyData(File.ReadAllBytes(fullPath), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signatureOk
                ? VerifyOutcome.Verified("Checksum OK. Signature OK.")
                : VerifyOutcome.Tampered("Checksum OK but signature verification FAILED — the manifest does not match this certificate.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return VerifyOutcome.Error($"Signature verification error: {ex.Message}");
        }
    }

    private static string ComputeHashHex(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string? ParseChecksumLine(string content)
    {
        var hex = content.AsSpan().TrimStart();
        var separator = hex.IndexOf(' ');
        return separator > 0 ? hex[..separator].ToString() : null;
    }
}
