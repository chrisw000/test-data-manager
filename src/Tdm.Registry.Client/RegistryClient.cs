using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Tdm.Registry.Client;

/// <summary>Result of a lock attempt: exactly one of <see cref="Granted"/> / <see cref="Conflict"/> is set.</summary>
public sealed record AcquireLockResult(LockGrantedResponse? Granted, LockConflictResponse? Conflict)
{
    public bool IsGranted => Granted is not null;
}

/// <summary>
/// HTTP client for a Tdm.Registry service (W2-D7). The caller owns the HttpClient (base
/// address = registry.url; aggressive timeout recommended — the registry must never become
/// the slow path of a seeding run). Auth: an optional API key sent as X-Tdm-ApiKey.
/// </summary>
public sealed class RegistryClient(HttpClient http, string? apiKey = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Guid> StartRunAsync(StartRunRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "runs", request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<StartRunResponse>(JsonOptions, ct).ConfigureAwait(false);
        return body!.Id;
    }

    public async Task FinishRunAsync(Guid runId, string outcome, string? manifestUrl, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Patch, $"runs/{runId}",
            new FinishRunRequest(outcome, manifestUrl), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AcquireLockResult> AcquireLockAsync(AcquireLockRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "locks", request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<LockConflictResponse>(JsonOptions, ct).ConfigureAwait(false);
            return new AcquireLockResult(null, conflict);
        }
        response.EnsureSuccessStatusCode();
        var granted = await response.Content.ReadFromJsonAsync<LockGrantedResponse>(JsonOptions, ct).ConfigureAwait(false);
        return new AcquireLockResult(granted, null);
    }

    public async Task<DateTime> HeartbeatAsync(Guid lockId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"locks/{lockId}/heartbeat", content: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, ct).ConfigureAwait(false);
        return body!.ExpiresUtc;
    }

    public async Task ReleaseLockAsync(Guid lockId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"locks/{lockId}", content: null, ct).ConfigureAwait(false);
        // 404 = already expired and reaped — releasing is best-effort by design.
        if (response.StatusCode != HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (content is not null) request.Content = JsonContent.Create(content, options: JsonOptions);
        if (!string.IsNullOrEmpty(apiKey)) request.Headers.Add("X-Tdm-ApiKey", apiKey);
        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }
}
