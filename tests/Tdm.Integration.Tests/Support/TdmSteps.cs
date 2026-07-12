using Microsoft.EntityFrameworkCore;
using Reqnroll;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.EfCore;
using Tdm.Identity;
using Tdm.Plugins;
using Xunit;

namespace Tdm.Integration.Tests.Support;

[Binding]
public sealed class TdmSteps(TdmHarness harness)
{
    private Guid _lastExternalId;
    private string? _firstRunEmail;
    private string? _secondRunEmail;

    // ---------------------------------------------------------------- Arrange

    [Given(@"^the failure policy is (BestEffort|FailObject|FailRun)$")]
    public void GivenFailurePolicy(FailurePolicy policy) => harness.Settings.Run.FailurePolicy = policy;

    [Given(@"^the lifecycle is (Persistent|Transactional|TrackedTeardown)$")]
    public void GivenLifecycle(LifecycleMode lifecycle) => harness.Settings.Run.Lifecycle = lifecycle;

    // ---------------------------------------------------------------- Act

    [When(@"I run the TDM with:")]
    [When(@"I run the TDM again with:")]
    public async Task WhenRunTdm(string tdmFeatureText) => await harness.RunAsync(tdmFeatureText);

    [When(@"I run the same TDM feature in two fresh environments with seed (\d+) and seed (\d+):")]
    public async Task WhenRunInTwoFreshEnvironments(int seedA, int seedB, string tdmFeatureText)
    {
        _firstRunEmail = await GeneratedEmail(seedA, tdmFeatureText);
        _secondRunEmail = await GeneratedEmail(seedB, tdmFeatureText);

        static async Task<string?> GeneratedEmail(int seed, string text)
        {
            using var environment = new TdmHarness();
            environment.Settings.Run.DefaultSeed = seed;
            var manifest = await environment.RunAsync(text);
            Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
            return manifest.Scenarios[0].Entities[0].Values["Email"];
        }
    }

    [When(@"I run the TDM with the Orders domain loaded as a plugin:")]
    public async Task WhenRunViaPlugin(string tdmFeatureText)
    {
        // Primary decoupled mode: assemblies come from a folder via an isolated
        // AssemblyLoadContext, not from compile-time references.
        var domain = harness.Settings.Domains[0];
        domain.PluginPath = Path.GetDirectoryName(typeof(Acme.Orders.Data.Persistence.OrdersDbContext).Assembly.Location)!;

        var loader = new PluginLoader(new FolderPluginAcquirer());
        var plugin = await loader.LoadAsync(domain);
        try
        {
            var runtime = DomainRuntimeBuilder.Build(domain, harness.Settings, plugin.Assemblies);
            await using (runtime.ConfigureAwait(false))
            {
                await harness.RunWithRuntimesAsync(tdmFeatureText, [runtime]);
            }
        }
        finally
        {
            plugin.LoadContext.Unload();
        }
    }

    // ---------------------------------------------------------------- Manifest assertions

    [Then(@"^the run outcome is (Succeeded|CompletedWithWarnings|Failed)$")]
    public void ThenRunOutcome(RunOutcome expected)
    {
        var manifest = harness.RequireManifest();
        var warnings = string.Join(" | ", manifest.Scenarios
            .SelectMany(s => s.Warnings.Concat(s.Entities.SelectMany(e => e.Warnings))));
        Assert.True(expected == manifest.Run.Outcome,
            $"Expected {expected} but was {manifest.Run.Outcome}. Warnings: {warnings}");
    }

    [Then(@"the exit code is (\d+)")]
    public void ThenExitCode(int expected) => Assert.Equal(expected, harness.RequireManifest().ExitCode);

    [Then(@"the manifest run executed (\d+) scenarios?")]
    public void ThenScenarioCount(int expected) => Assert.Equal(expected, harness.RequireManifest().Scenarios.Count);

    [Then(@"the manifest records a reference resolved from ""(.*)""")]
    public void ThenReferenceResolvedFrom(string source) =>
        Assert.Contains(harness.RequireManifest().Scenarios.SelectMany(s => s.References),
            r => r.ResolvedFrom == source);

    [Then(@"a manifest warning contains ""(.*)""")]
    public void ThenWarningContains(string fragment)
    {
        var manifest = harness.RequireManifest();
        var allWarnings = manifest.Scenarios.SelectMany(s => s.Warnings)
            .Concat(manifest.Scenarios.SelectMany(s => s.Entities).SelectMany(e => e.Warnings));
        Assert.Contains(allWarnings, w => w.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the manifest entity ""(.*)"" was persisted via ""(.*)""")]
    public void ThenPersistedVia(string entity, string routeFragment) =>
        Assert.Contains(harness.RequireManifest().Scenarios.SelectMany(s => s.Entities),
            e => e.Entity == entity && e.PersistedVia?.Contains(routeFragment) == true);

    [Then(@"the manifest benchmark includes ""(.*)""")]
    public void ThenBenchmarkIncludes(string operation) =>
        Assert.Contains(operation, harness.RequireManifest().Run.Benchmark.Keys);

    [Then(@"the manifest has (\d+) unmatched steps?")]
    public void ThenUnmatchedSteps(int expected) =>
        Assert.Equal(expected, harness.RequireManifest().Scenarios.Sum(s => s.UnmatchedSteps.Count));

    [Then(@"the manifest scenario teardown deleted (\d+) rows")]
    public void ThenTeardownDeleted(int expected)
    {
        var scenario = Assert.Single(harness.RequireManifest().Scenarios);
        Assert.NotNull(scenario.Teardown);
        Assert.Equal(expected, scenario.Teardown!.Deleted);
        Assert.Empty(scenario.Teardown.Orphaned);
    }

    [Then(@"the manifest invoice id is a database-generated integer")]
    public void ThenInvoiceIdIsDbGenerated()
    {
        var entry = harness.RequireManifest().Scenarios.SelectMany(s => s.Entities).First(e => e.Entity == "Invoice");
        Assert.Equal("DbGenerated", entry.IdStrategy);
        Assert.True(int.Parse(entry.Id!) > 0);
    }

    // ---------------------------------------------------------------- Database assertions

    [Then(@"the Orders database has (\d+) customer rows")]
    public async Task ThenCustomerCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        Assert.Equal(expected, await db.Customers.CountAsync());
    }

    [Then(@"the Orders database has (\d+) product rows")]
    public async Task ThenProductCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        Assert.Equal(expected, await db.Products.CountAsync());
    }

    [Then(@"the Orders database has (\d+) order rows")]
    public async Task ThenOrderCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        Assert.Equal(expected, await db.Orders.CountAsync());
    }

    [Then(@"customer ""(.*)"" has tier ""(.*)""")]
    public async Task ThenCustomerTier(string name, string tier)
    {
        await using var db = harness.NewOrdersContext();
        var customer = await db.Customers.SingleAsync(c => c.Name == name);
        Assert.Equal(tier, customer.Tier);
    }

    [Then(@"order ""(.*)"" is linked to customer ""(.*)""")]
    public async Task ThenOrderLinked(string orderNumber, string customerName)
    {
        await using var db = harness.NewOrdersContext();
        var order = await db.Orders.SingleAsync(o => o.OrderNumber == orderNumber);
        var customer = await db.Customers.SingleAsync(c => c.Name == customerName);
        Assert.Equal(customer.Id, order.CustomerId);
    }

    [Then(@"customer ""(.*)"" has the identity-contract id for domain ""(.*)""")]
    public async Task ThenCustomerIdentity(string name, string domain)
    {
        await using var db = harness.NewOrdersContext();
        var customer = await db.Customers.SingleAsync(c => c.Name == name);
        Assert.Equal(TdmIdentity.ForNaturalKey(domain, "Customer", name), customer.Id);
    }

    [Then(@"invoice ""(.*)"" carries the external customer id for ""(.*)"" from domain ""(.*)""")]
    public async Task ThenInvoiceExternalCustomer(string invoiceNumber, string customerKey, string domain)
    {
        _lastExternalId = TdmIdentity.ForNaturalKey(domain, "Customer", customerKey);
        await using var db = harness.NewBillingContext();
        var invoice = await db.Invoices.SingleAsync(i => i.InvoiceNumber == invoiceNumber);
        Assert.Equal(_lastExternalId, invoice.CustomerId);
    }

    [Then(@"a customer summary projection ""(.*)"" exists with that id")]
    public async Task ThenProjectionExists(string name)
    {
        await using var db = harness.NewBillingContext();
        var summary = await db.CustomerSummaries.SingleAsync(s => s.Name == name);
        Assert.Equal(_lastExternalId, summary.Id);
    }

    // ---------------------------------------------------------------- Determinism

    [Then(@"the generated customer emails match")]
    public void ThenEmailsMatch()
    {
        Assert.NotNull(_firstRunEmail);
        Assert.Equal(_firstRunEmail, _secondRunEmail);
    }

    [Then(@"the generated customer emails differ")]
    public void ThenEmailsDiffer()
    {
        Assert.NotNull(_firstRunEmail);
        Assert.NotEqual(_firstRunEmail, _secondRunEmail);
    }
}
