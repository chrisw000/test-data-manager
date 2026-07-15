using AwesomeAssertions;
using Tdm.Core.Secrets;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Secrets;

public class SecretChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Chain_FirstNonNullWins_InlineBeforeEnv()
    {
        var chain = new SecretChain(
        [
            new InlineSecretProvider(new Dictionary<string, string> { ["Shared"] = "from-inline" }),
            new EnvironmentSecretProvider(key => key == "Shared" ? "from-env" : key == "EnvOnly" ? "env-value" : null),
        ]);

        (await chain.GetSecretAsync("Shared", Ct)).Should().Be("from-inline");
        (await chain.GetSecretAsync("EnvOnly", Ct)).Should().Be("env-value");
        (await chain.GetSecretAsync("Nowhere", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task EnvironmentProvider_TreatsEmptyAsMissing()
    {
        var provider = new EnvironmentSecretProvider(_ => "");
        (await provider.GetSecretAsync("Anything", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task GetFirst_TriesCandidateSpellingsInOrder()
    {
        var chain = new SecretChain(
        [
            new InlineSecretProvider(new Dictionary<string, string> { ["ConnectionStrings__OrdersDb"] = "second-spelling" }),
        ]);
        var value = await chain.GetFirstAsync(
            ["TDM_CONNECTIONSTRINGS__ORDERSDB", "ConnectionStrings__OrdersDb", "OrdersDb"], Ct);
        value.Should().Be("second-spelling");
    }

    [Fact]
    public void Factory_Default_EnvironmentOnly()
    {
        var chain = SecretChainFactory.Create(new SecretsSettings());
        chain.Providers.Should().ContainSingle().Which.Name.Should().Be("env");
    }

    [Fact]
    public void Factory_InlineSecretsConfigured_InlineTriedFirst()
    {
        var chain = SecretChainFactory.Create(new SecretsSettings { Inline = { ["X"] = "y" } });
        chain.Providers.Select(p => p.Name).Should().Equal("inline", "env");
    }

    [Fact]
    public void Factory_CloudProviderNamed_WithoutAdapter_ThrowsWithGuidance() =>
        FluentActions.Invoking(() => SecretChainFactory.Create(new SecretsSettings { Provider = "AzureKeyVault" }))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AzureKeyVault").And.Contain("ISecretProvider");

    [Fact]
    public async Task Factory_CloudProviderNamed_WithMatchingAdapter_AppendedToChain()
    {
        var adapter = new FakeVaultProvider();
        var chain = SecretChainFactory.Create(new SecretsSettings { Provider = "AzureKeyVault" }, adapter);
        chain.Providers.Select(p => p.Name).Should().Equal("env", "AzureKeyVault");
        (await chain.GetSecretAsync("vault-key", Ct)).Should().Be("vault-value");
    }

    private sealed class FakeVaultProvider : ISecretProvider
    {
        public string Name => "AzureKeyVault";
        public ValueTask<string?> GetSecretAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(key == "vault-key" ? "vault-value" : null);
    }
}
