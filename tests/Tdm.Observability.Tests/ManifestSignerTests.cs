using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Tdm.Core.Settings;
using Tdm.Observability.Audit;
using Xunit;

namespace Tdm.Observability.Tests;

public class ManifestSignerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> WriteManifestAsync(string content = "{ \"run\": { \"name\": \"x\" } }")
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-signer-{Guid.NewGuid():N}.tdm.json");
        await File.WriteAllTextAsync(path, content, Ct);
        return path;
    }

    [Fact]
    public async Task WriteChecksum_MatchesFileHash_InSha256sumFormat()
    {
        var manifest = await WriteManifestAsync();
        try
        {
            var checksumPath = ManifestSigner.WriteChecksum(manifest);
            var line = await File.ReadAllTextAsync(checksumPath, Ct);
            var expectedHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(manifest, Ct)));
            line.Should().Be($"{expectedHash}  {Path.GetFileName(manifest)}\n");
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); }
    }

    [Fact]
    public async Task Verify_ChecksumOnly_NoSignatureFile_ReturnsVerified()
    {
        var manifest = await WriteManifestAsync();
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            var outcome = ManifestSigner.Verify(manifest, publicCertPath: null);
            outcome.Status.Should().Be(VerifyStatus.Verified);
            outcome.ExitCode.Should().Be(0);
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); }
    }

    [Fact]
    public async Task Verify_TamperedManifest_ReturnsTampered()
    {
        var manifest = await WriteManifestAsync();
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            await File.AppendAllTextAsync(manifest, " ", Ct); // single-byte edit after checksumming

            var outcome = ManifestSigner.Verify(manifest, publicCertPath: null);
            outcome.Status.Should().Be(VerifyStatus.Tampered);
            outcome.ExitCode.Should().Be(2);
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); }
    }

    [Fact]
    public async Task Verify_MissingChecksumFile_ReturnsError()
    {
        var manifest = await WriteManifestAsync();
        try
        {
            var outcome = ManifestSigner.Verify(manifest, publicCertPath: null);
            outcome.Status.Should().Be(VerifyStatus.Error);
            outcome.Message.Should().Contain("No checksum file found");
        }
        finally { File.Delete(manifest); }
    }

    [Fact]
    public void Verify_MissingManifest_ReturnsError()
    {
        var outcome = ManifestSigner.Verify(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json"), null);
        outcome.Status.Should().Be(VerifyStatus.Error);
    }

    // ---------------------------------------------------------------- Signing round-trip (real ephemeral cert)

    [Fact]
    public async Task Sign_And_Verify_RoundTrip_Succeeds()
    {
        var manifest = await WriteManifestAsync();
        using var fixture = new SigningCertFixture();
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            ManifestSigner.Sign(manifest, fixture.PfxPath, fixture.Password);

            var outcome = ManifestSigner.Verify(manifest, fixture.PublicCertPath);
            outcome.Status.Should().Be(VerifyStatus.Verified);
            outcome.ExitCode.Should().Be(0);
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); File.Delete(manifest + ".sig"); }
    }

    [Fact]
    public async Task Verify_SignaturePresent_NoCertGiven_ReturnsPartiallyVerified()
    {
        var manifest = await WriteManifestAsync();
        using var fixture = new SigningCertFixture();
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            ManifestSigner.Sign(manifest, fixture.PfxPath, fixture.Password);

            var outcome = ManifestSigner.Verify(manifest, publicCertPath: null);
            outcome.Status.Should().Be(VerifyStatus.PartiallyVerified);
            outcome.ExitCode.Should().Be(1);
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); File.Delete(manifest + ".sig"); }
    }

    [Fact]
    public async Task Verify_WrongCert_ReturnsTampered()
    {
        var manifest = await WriteManifestAsync();
        using var signingFixture = new SigningCertFixture();
        using var otherFixture = new SigningCertFixture(); // different key pair
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            ManifestSigner.Sign(manifest, signingFixture.PfxPath, signingFixture.Password);

            var outcome = ManifestSigner.Verify(manifest, otherFixture.PublicCertPath);
            outcome.Status.Should().Be(VerifyStatus.Tampered);
        }
        finally { File.Delete(manifest); File.Delete(manifest + ".sha256"); File.Delete(manifest + ".sig"); }
    }

    [Fact]
    public void Sign_WrongPassword_Throws()
    {
        using var fixture = new SigningCertFixture();
        FluentActions.Invoking(() => ManifestSigner.Sign("irrelevant.json", fixture.PfxPath, "wrong-password"))
            .Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task SignFromSettings_ResolvesPasswordFromEnvVar()
    {
        var manifest = await WriteManifestAsync();
        using var fixture = new SigningCertFixture();
        var envVarName = $"TDM_TEST_SIGNING_PW_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVarName, fixture.Password);
        try
        {
            ManifestSigner.WriteChecksum(manifest);
            var signing = new SigningSettings { CertificatePath = fixture.PfxPath, CertificatePasswordEnv = envVarName };
            ManifestSigner.SignFromSettings(manifest, signing);

            ManifestSigner.Verify(manifest, fixture.PublicCertPath).Status.Should().Be(VerifyStatus.Verified);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, null);
            File.Delete(manifest); File.Delete(manifest + ".sha256"); File.Delete(manifest + ".sig");
        }
    }
}

/// <summary>An in-memory-generated self-signed RSA certificate, exported to a temp .pfx
/// (private key, for signing) and a temp public .cer (for verification) — no fixture
/// certificate checked into the repo.</summary>
internal sealed class SigningCertFixture : IDisposable
{
    public string PfxPath { get; }
    public string PublicCertPath { get; }
    public string Password { get; } = "test-password";

    public SigningCertFixture()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=tdm-test-signing", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        PfxPath = Path.Combine(Path.GetTempPath(), $"tdm-signing-{Guid.NewGuid():N}.pfx");
        PublicCertPath = Path.Combine(Path.GetTempPath(), $"tdm-signing-{Guid.NewGuid():N}.cer");
        File.WriteAllBytes(PfxPath, cert.Export(X509ContentType.Pfx, Password));
        File.WriteAllBytes(PublicCertPath, cert.Export(X509ContentType.Cert));
    }

    public void Dispose()
    {
        try { File.Delete(PfxPath); } catch { /* best effort */ }
        try { File.Delete(PublicCertPath); } catch { /* best effort */ }
    }
}
