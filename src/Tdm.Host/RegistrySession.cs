using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Tdm.Core.Settings;
using Tdm.Registry.Client;

namespace Tdm.Host;

/// <summary>
/// One run's interaction with the run registry (W2-D7): registers the run, acquires a lease
/// on every domain's (environment, domain, database) before seeding, heartbeats while the
/// run executes, and releases/finishes on dispose. A lock conflict always refuses the run
/// naming the holder; an unreachable registry degrades per registry.unavailable
/// (Warn: continue without locks · Fail: refuse).
/// </summary>
internal sealed class RegistrySession : IAsyncDisposable
{
    private readonly RegistryClient _client;
    private readonly ILogger _log;
    private readonly List<Guid> _lockIds = [];
    private readonly CancellationTokenSource _heartbeatCts = new();
    private Task? _heartbeatTask;
    private bool _finished;

    public Guid? RunId { get; private set; }

    /// <summary>Set when the session could not start: the caller must refuse the run (exit 2).</summary>
    public string? RefusalReason { get; private init; }

    private RegistrySession(RegistryClient client, ILogger log)
    {
        _client = client;
        _log = log;
    }

    private RegistrySession(string refusalReason)
    {
        _client = null!;
        _log = null!;
        RefusalReason = refusalReason;
    }

    /// <summary>Disabled session — registry not configured, or unreachable with Unavailable=Warn.</summary>
    private static readonly RegistrySession Disabled = new((RegistryClient)null!, null!);

    /// <param name="apiKey">Pre-resolved by the caller through the secret chain (W2-D8).</param>
    public static async Task<RegistrySession> StartAsync(TdmSettings settings, string? environmentName,
        string? runnerId, string? apiKey, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Registry.Url)) return Disabled;

        var registry = settings.Registry;
        var http = new HttpClient
        {
            BaseAddress = new Uri(registry.Url.TrimEnd('/') + "/"),
            // The registry must never become the slow path of a seeding run.
            Timeout = TimeSpan.FromSeconds(10),
        };
        var client = new RegistryClient(http, apiKey);
        var session = new RegistrySession(client, log);

        try
        {
            session.RunId = await client.StartRunAsync(new StartRunRequest(
                environmentName, settings.Run.Name, null, runnerId), ct).ConfigureAwait(false);

            var holder = $"{settings.Run.Name} ({runnerId})";
            foreach (var domain in settings.Domains)
            {
                var result = await client.AcquireLockAsync(new AcquireLockRequest(
                    environmentName, domain.Name, DatabaseIdFor(domain), session.RunId, holder,
                    registry.LockTtlSeconds), ct).ConfigureAwait(false);

                if (!result.IsGranted)
                {
                    var conflict = result.Conflict!;
                    await session.ReleaseAllAsync().ConfigureAwait(false);
                    await session.FinishAsync("LockConflict", null, ct).ConfigureAwait(false);
                    return new RegistrySession(
                        $"Database for domain '{domain.Name}' is locked by {conflict.Holder ?? "another run"} " +
                        $"(acquired {conflict.AcquiredUtc:u}, lease expires {conflict.ExpiresUtc:u}). " +
                        "Wait for that run to finish or for the lease to expire.");
                }
                session._lockIds.Add(result.Granted!.Id);
                log.LogInformation("Registry lock acquired for {Domain} (expires {Expires:u})",
                    domain.Name, result.Granted.ExpiresUtc);
            }

            session._heartbeatTask = session.HeartbeatLoopAsync(
                TimeSpan.FromSeconds(Math.Max(5, registry.HeartbeatSeconds)));
            return session;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            await session.ReleaseAllAsync().ConfigureAwait(false);
            if (registry.Unavailable == RegistryUnavailableBehavior.Fail)
                return new RegistrySession($"Run registry at '{registry.Url}' is unreachable ({ex.Message}) and registry.unavailable is Fail.");
            log.LogWarning("Run registry at {Url} is unreachable ({Message}) — continuing without registry/locks (registry.unavailable: Warn).",
                registry.Url, ex.Message);
            return Disabled;
        }
    }

    /// <summary>The lease key never contains the connection string itself — only a hash.
    /// Teams must use byte-identical connection strings for the collision guarantee to hold.</summary>
    internal static string DatabaseIdFor(DomainSettings domain)
    {
        var connection = domain.ResolveConnectionString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{domain.Provider}|{connection}")))[..32];
    }

    public async Task FinishAsync(string outcome, string? manifestUrl, CancellationToken ct)
    {
        if (RunId is not { } runId || _finished) return;
        _finished = true;
        try
        {
            await _client.FinishRunAsync(runId, outcome, manifestUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Could not report run finish to the registry: {Message}", ex.Message);
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_heartbeatCts.Token).ConfigureAwait(false))
            {
                foreach (var lockId in _lockIds)
                {
                    try
                    {
                        await _client.HeartbeatAsync(lockId, _heartbeatCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        _log.LogWarning("Registry heartbeat failed for lock {LockId}: {Message}", lockId, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session ending — normal.
        }
    }

    private async Task ReleaseAllAsync()
    {
        foreach (var lockId in _lockIds)
        {
            try { await _client.ReleaseLockAsync(lockId).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning("Could not release registry lock {LockId} ({Message}) — the lease will expire by TTL.", lockId, ex.Message);
            }
        }
        _lockIds.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(this, Disabled) || RefusalReason is not null) return;
        await _heartbeatCts.CancelAsync().ConfigureAwait(false);
        if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false);
        _heartbeatCts.Dispose();
        await ReleaseAllAsync().ConfigureAwait(false);
        // A run that never reported an outcome (exception path) is marked aborted, not left dangling.
        await FinishAsync("Aborted", null, CancellationToken.None).ConfigureAwait(false);
    }
}
