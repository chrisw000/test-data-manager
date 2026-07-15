namespace Tdm.Registry.Client;

// Wire contracts shared between Tdm.Registry (service) and RegistryClient (host side).
// The registry is a thin lease + index service (W2-D7): it links to manifests, it never
// duplicates their content.

public sealed record StartRunRequest(
    string? Environment, string Name, string? SettingsSha256, string? RunnerId);

public sealed record StartRunResponse(Guid Id);

public sealed record FinishRunRequest(string Outcome, string? ManifestUrl);

public sealed record RunRecord(
    Guid Id, string? Environment, string Name, string? SettingsSha256, string? RunnerId,
    DateTime StartedUtc, DateTime? FinishedUtc, string? Outcome, string? ManifestUrl);

public sealed record AcquireLockRequest(
    string? Environment, string Domain, string DatabaseId, Guid? RunId, string? Holder, int TtlSeconds);

public sealed record LockGrantedResponse(Guid Id, DateTime ExpiresUtc);

/// <summary>Returned with 409 Conflict: who holds the lease — the "clear error naming the
/// holding run/team" the host surfaces on collision.</summary>
public sealed record LockConflictResponse(
    string? Environment, string Domain, string DatabaseId, string? Holder, Guid? RunId,
    DateTime AcquiredUtc, DateTime ExpiresUtc);

public sealed record LockRecord(
    Guid Id, string? Environment, string Domain, string DatabaseId, string? Holder, Guid? RunId,
    DateTime AcquiredUtc, DateTime ExpiresUtc);

public sealed record HeartbeatResponse(DateTime ExpiresUtc);
