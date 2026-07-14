using System.Security.Cryptography;
using AwesomeAssertions;
using Tdm.Observability.Audit;
using Xunit;

namespace Tdm.Observability.Tests;

public class AttributionCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------- DetectRunnerId (pure)

    [Fact]
    public void DetectRunnerId_GitHubActions_FormatsRunUrl()
    {
        var env = new Dictionary<string, string?>
        {
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_SERVER_URL"] = "https://github.com",
            ["GITHUB_REPOSITORY"] = "chrisw000/test-data-manager",
            ["GITHUB_RUN_ID"] = "12345",
            ["GITHUB_ACTOR"] = "chrisw000",
        };
        AttributionCollector.DetectRunnerId(k => env.GetValueOrDefault(k), "ignored")
            .Should().Be("github-actions:https://github.com/chrisw000/test-data-manager/actions/runs/12345#chrisw000");
    }

    [Fact]
    public void DetectRunnerId_GenericCi_UsesJobUrlAndActor()
    {
        var env = new Dictionary<string, string?> { ["CI"] = "true", ["BUILD_URL"] = "https://ci.example/job/42" };
        AttributionCollector.DetectRunnerId(k => env.GetValueOrDefault(k), "fallback-user")
            .Should().Be("ci:https://ci.example/job/42#fallback-user");
    }

    [Fact]
    public void DetectRunnerId_NoCiEnv_FallsBackToLocalUser() =>
        AttributionCollector.DetectRunnerId(_ => null, "chris").Should().Be("local:chris");

    // ---------------------------------------------------------------- TryGetGitInfo (real git)

    [Fact]
    public void TryGetGitInfo_CleanRepo_ReturnsShaAndNotDirty()
    {
        using var repo = new TempGitRepo();
        var (sha, dirty) = AttributionCollector.TryGetGitInfo(repo.Directory);
        sha.Should().Be(repo.HeadSha);
        dirty.Should().BeFalse();
    }

    [Fact]
    public void TryGetGitInfo_UntrackedFile_ReturnsDirtyTrue()
    {
        using var repo = new TempGitRepo();
        File.WriteAllText(Path.Combine(repo.Directory, "untracked.txt"), "x");
        var (sha, dirty) = AttributionCollector.TryGetGitInfo(repo.Directory);
        sha.Should().Be(repo.HeadSha);
        dirty.Should().BeTrue();
    }

    [Fact]
    public void TryGetGitInfo_NotARepo_ReturnsNulls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tdm-not-a-repo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (sha, dirty) = AttributionCollector.TryGetGitInfo(directory);
            sha.Should().BeNull();
            dirty.Should().BeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // ---------------------------------------------------------------- Collect (end to end)

    [Fact]
    public async Task Collect_ComputesSettingsHash_AndPopulatesHostname()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-attribution-{Guid.NewGuid():N}.json");
        var content = "{ \"run\": { \"name\": \"x\" } }";
        await File.WriteAllTextAsync(path, content, Ct);
        try
        {
            var attribution = AttributionCollector.Collect(path);
            attribution.SettingsFileSha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
            attribution.Hostname.Should().NotBeNullOrEmpty();
            attribution.RunnerId.Should().NotBeNullOrEmpty();
        }
        finally { File.Delete(path); }
    }
}

/// <summary>A real temp git repo with one commit — exercised over the actual `git` binary
/// (already a build/CI dependency for this project, so no fake/mocking needed).</summary>
internal sealed class TempGitRepo : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tdm-git-fixture", Guid.NewGuid().ToString("N"));
    public string HeadSha { get; }

    public TempGitRepo()
    {
        System.IO.Directory.CreateDirectory(Directory);
        RunGit("init -q -b main");
        RunGit("-c user.email=tdm@test -c user.name=tdm commit --allow-empty -q -m init");
        HeadSha = RunGit("rev-parse HEAD").Trim();
    }

    private string RunGit(string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = Directory,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best effort */ }
    }
}
