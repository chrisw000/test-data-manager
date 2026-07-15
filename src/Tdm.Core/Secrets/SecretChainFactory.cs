using Tdm.Core.Settings;

namespace Tdm.Core.Secrets;

/// <summary>
/// Builds the secret-resolution chain from settings (W2-D8): inline (dev only, when
/// configured) → environment → a named cloud provider. TDM ships no cloud SDKs — naming
/// "AzureKeyVault"/"AwsSecretsManager" (or anything else) in settings requires the matching
/// <see cref="ISecretProvider"/> to be supplied by the embedding host; the CLI fails fast
/// with guidance otherwise. Ambient credentials are the adapter's concern; TDM stores nothing.
/// </summary>
public static class SecretChainFactory
{
    public const string EnvironmentProviderName = "Environment";

    public static SecretChain Create(SecretsSettings settings, params ISecretProvider[] additionalProviders)
    {
        var providers = new List<ISecretProvider>();
        if (settings.Inline.Count > 0)
            providers.Add(new InlineSecretProvider(settings.Inline));
        providers.Add(new EnvironmentSecretProvider());

        if (!string.Equals(settings.Provider, EnvironmentProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var named = additionalProviders.FirstOrDefault(p =>
                string.Equals(p.Name, settings.Provider, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"secrets.provider is '{settings.Provider}' but no such ISecretProvider is available. " +
                    "TDM ships the Environment provider only; cloud adapters (Azure Key Vault, AWS Secrets Manager) " +
                    "implement Tdm.Core.Secrets.ISecretProvider and are registered by the embedding host — " +
                    "see docs/secrets-and-playback.md.");
            providers.Add(named);
        }
        return new SecretChain(providers);
    }
}
