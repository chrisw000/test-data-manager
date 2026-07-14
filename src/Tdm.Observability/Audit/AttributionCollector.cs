using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Tdm.Core.Manifest;

namespace Tdm.Observability.Audit;

/// <summary>
/// Collects <see cref="AttributionInfo"/> at manifest-build time (W2-D1): who/what ran this,
/// the git state of the settings/feature files' repo, and a hash of the settings file as
/// loaded. Every source degrades gracefully (git absent, not a repo, hostname unavailable)
/// rather than failing the run — attribution is best-effort evidence, not a hard requirement.
/// </summary>
public static class AttributionCollector
{
    public static AttributionInfo Collect(string settingsFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsFilePath)) ?? Directory.GetCurrentDirectory();
        var (sha, dirty) = TryGetGitInfo(directory);
        return new AttributionInfo
        {
            RunnerId = DetectRunnerId(Environment.GetEnvironmentVariable, Environment.UserName),
            Hostname = TryGetHostname(),
            GitSha = sha,
            GitDirty = dirty,
            SettingsFileSha256 = TryComputeFileHash(settingsFilePath),
        };
    }

    /// <summary>Pure — takes an env lookup and local username so it's testable without touching the real process environment.</summary>
    public static string DetectRunnerId(Func<string, string?> env, string localUserName)
    {
        if (string.Equals(env("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var server = env("GITHUB_SERVER_URL") ?? "https://github.com";
            var repo = env("GITHUB_REPOSITORY") ?? "unknown/unknown";
            var runId = env("GITHUB_RUN_ID") ?? "0";
            var actor = env("GITHUB_ACTOR") ?? "unknown";
            return $"github-actions:{server}/{repo}/actions/runs/{runId}#{actor}";
        }

        // Generic CI fallback — most CI systems set CI=true; job-URL env var names vary,
        // so try the common ones before falling back to just "ci:unknown".
        if (!string.IsNullOrEmpty(env("CI")))
        {
            var jobUrl = env("CI_JOB_URL") ?? env("BUILD_URL") ?? env("SYSTEM_TASKDEFINITIONSURI") ?? "unknown";
            var actor = env("CI_COMMIT_AUTHOR") ?? env("BUILD_REQUESTEDFOR") ?? localUserName;
            return $"ci:{jobUrl}#{actor}";
        }

        return $"local:{localUserName}";
    }

    private static string TryGetHostname()
    {
        try { return Environment.MachineName; }
        catch (InvalidOperationException) { return "unknown"; }
    }

    /// <summary>Shells out to `git`; returns (null, null) if unavailable or not a repo.</summary>
    public static (string? Sha, bool? Dirty) TryGetGitInfo(string directory)
    {
        var sha = RunGit(directory, "rev-parse HEAD")?.Trim();
        if (string.IsNullOrEmpty(sha)) return (null, null);
        var status = RunGit(directory, "status --porcelain");
        return (sha, status is not null && status.Trim().Length > 0);
    }

    private static string? RunGit(string workingDirectory, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            return null; // git not on PATH
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? TryComputeFileHash(string path)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); }
        catch (IOException) { return null; }
    }
}
