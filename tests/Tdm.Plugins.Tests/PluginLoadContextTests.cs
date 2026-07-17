using AwesomeAssertions;
using Tdm.Plugins;
using Xunit;

namespace Tdm.Plugins.Tests;

public class PluginLoadContextTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore", true)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", true)]
    [InlineData("Tdm.Core", true)]
    [InlineData("Tdm.EfCore", true)]
    [InlineData("Bogus", true)]
    [InlineData("Acme.Orders.Data.Persistence", false)]
    [InlineData("Npgsql", false)]
    // Provider plugin packages (W3-D5) are folder-loaded, not host-provided — the exception
    // to the Tdm.* sharing rule.
    [InlineData("Tdm.Providers.PostgreSql", false)]
    public void IsShared_SharesHostSurface_ButNotProviderPlugins(string assemblyName, bool shared) =>
        PluginLoadContext.IsShared(assemblyName).Should().Be(shared);
}
