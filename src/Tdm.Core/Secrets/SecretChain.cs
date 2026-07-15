namespace Tdm.Core.Secrets;

/// <summary>
/// One source of secret values in the resolution chain (W2-D8). Implementations must be
/// read-only and side-effect free; TDM never writes or caches secrets to disk.
/// Cloud adapters (Azure Key Vault, AWS Secrets Manager) implement this interface and are
/// passed to <see cref="SecretChainFactory.Create"/> — TDM ships no cloud SDK dependencies.
/// </summary>
public interface ISecretProvider
{
    /// <summary>Short source name recorded in errors/logs: "inline", "env", "AzureKeyVault", …</summary>
    string Name { get; }

    /// <summary>The secret value, or null when this provider doesn't hold the key.</summary>
    ValueTask<string?> GetSecretAsync(string key, CancellationToken ct = default);
}

/// <summary>Secrets inlined in tdm.settings.json — development only; environment policy can
/// ban inline connection-string sources (tdm.policy.json connectionStringSources).</summary>
public sealed class InlineSecretProvider(IReadOnlyDictionary<string, string> secrets) : ISecretProvider
{
    public string Name => "inline";

    public ValueTask<string?> GetSecretAsync(string key, CancellationToken ct = default) =>
        ValueTask.FromResult(secrets.TryGetValue(key, out var value) ? value : null);
}

/// <summary>Environment variables, looked up by exact key.</summary>
public sealed class EnvironmentSecretProvider(Func<string, string?>? getEnv = null) : ISecretProvider
{
    private readonly Func<string, string?> _getEnv = getEnv ?? Environment.GetEnvironmentVariable;

    public string Name => "env";

    public ValueTask<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        var value = _getEnv(key);
        return ValueTask.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }
}

/// <summary>Providers probed in order; first non-null value wins.</summary>
public sealed class SecretChain(IReadOnlyList<ISecretProvider> providers)
{
    public IReadOnlyList<ISecretProvider> Providers => providers;

    public async ValueTask<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        foreach (var provider in providers)
        {
            var value = await provider.GetSecretAsync(key, ct).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }

    /// <summary>Tries candidate key spellings in order (e.g. the connection-string naming
    /// conventions), returning the first value the chain resolves.</summary>
    public async ValueTask<string?> GetFirstAsync(IEnumerable<string> candidateKeys, CancellationToken ct = default)
    {
        foreach (var key in candidateKeys)
        {
            var value = await GetSecretAsync(key, ct).ConfigureAwait(false);
            if (value is not null) return value;
        }
        return null;
    }
}
