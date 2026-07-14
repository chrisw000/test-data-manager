using AwesomeAssertions;
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
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
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
        var plugin = await loader.LoadAsync(domain, Ct);
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
        manifest.Run.Outcome.Should().Be(expected, "warnings: {0}", warnings);
    }

    [Then(@"the exit code is (\d+)")]
    public void ThenExitCode(int expected) => harness.RequireManifest().ExitCode.Should().Be(expected);

    [Then(@"the manifest run executed (\d+) scenarios?")]
    public void ThenScenarioCount(int expected) => harness.RequireManifest().Scenarios.Should().HaveCount(expected);

    [Then(@"the manifest records a reference resolved from ""(.*)""")]
    public void ThenReferenceResolvedFrom(string source) =>
        harness.RequireManifest().Scenarios.SelectMany(s => s.References)
            .Should().Contain(r => r.ResolvedFrom == source);

    [Then(@"a manifest warning contains ""(.*)""")]
    public void ThenWarningContains(string fragment)
    {
        var manifest = harness.RequireManifest();
        var allWarnings = manifest.Scenarios.SelectMany(s => s.Warnings)
            .Concat(manifest.Scenarios.SelectMany(s => s.Entities).SelectMany(e => e.Warnings));
        allWarnings.Should().Contain(w => w.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the manifest entity ""(.*)"" was persisted via ""(.*)""")]
    public void ThenPersistedVia(string entity, string routeFragment) =>
        harness.RequireManifest().Scenarios.SelectMany(s => s.Entities)
            .Should().Contain(e => e.Entity == entity && e.PersistedVia != null && e.PersistedVia.Contains(routeFragment));

    [Then(@"the manifest benchmark includes ""(.*)""")]
    public void ThenBenchmarkIncludes(string operation) =>
        harness.RequireManifest().Run.Benchmark.Keys.Should().Contain(operation);

    [Then(@"the manifest has (\d+) unmatched steps?")]
    public void ThenUnmatchedSteps(int expected) =>
        harness.RequireManifest().Scenarios.Sum(s => s.UnmatchedSteps.Count).Should().Be(expected);

    [Then(@"the manifest scenario teardown deleted (\d+) rows")]
    public void ThenTeardownDeleted(int expected)
    {
        var scenario = harness.RequireManifest().Scenarios.Should().ContainSingle().Subject;
        scenario.Teardown.Should().NotBeNull();
        scenario.Teardown!.Deleted.Should().Be(expected);
        scenario.Teardown.Orphaned.Should().BeEmpty();
    }

    [Then(@"the manifest invoice id is a database-generated integer")]
    public void ThenInvoiceIdIsDbGenerated()
    {
        var entry = harness.RequireManifest().Scenarios.SelectMany(s => s.Entities).First(e => e.Entity == "Invoice");
        entry.IdStrategy.Should().Be("DbGenerated");
        int.Parse(entry.Id!).Should().BePositive();
    }

    // ---------------------------------------------------------------- Database assertions

    [Then(@"the Orders database has (\d+) customer rows")]
    public async Task ThenCustomerCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        (await db.Customers.CountAsync(Ct)).Should().Be(expected);
    }

    [Then(@"the Orders database has (\d+) product rows")]
    public async Task ThenProductCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        (await db.Products.CountAsync(Ct)).Should().Be(expected);
    }

    [Then(@"the Orders database has (\d+) order rows")]
    public async Task ThenOrderCount(int expected)
    {
        await using var db = harness.NewOrdersContext();
        (await db.Orders.CountAsync(Ct)).Should().Be(expected);
    }

    [Then(@"customer ""(.*)"" has tier ""(.*)""")]
    public async Task ThenCustomerTier(string name, string tier)
    {
        await using var db = harness.NewOrdersContext();
        var customer = await db.Customers.SingleAsync(c => c.Name == name, Ct);
        customer.Tier.Should().Be(tier);
    }

    [Then(@"order ""(.*)"" is linked to customer ""(.*)""")]
    public async Task ThenOrderLinked(string orderNumber, string customerName)
    {
        await using var db = harness.NewOrdersContext();
        var order = await db.Orders.SingleAsync(o => o.OrderNumber == orderNumber, Ct);
        var customer = await db.Customers.SingleAsync(c => c.Name == customerName, Ct);
        order.CustomerId.Should().Be(customer.Id);
    }

    [Then(@"customer ""(.*)"" has the identity-contract id for domain ""(.*)""")]
    public async Task ThenCustomerIdentity(string name, string domain)
    {
        await using var db = harness.NewOrdersContext();
        var customer = await db.Customers.SingleAsync(c => c.Name == name, Ct);
        customer.Id.Should().Be(TdmIdentity.ForNaturalKey(domain, "Customer", name));
    }

    [Then(@"invoice ""(.*)"" carries the external customer id for ""(.*)"" from domain ""(.*)""")]
    public async Task ThenInvoiceExternalCustomer(string invoiceNumber, string customerKey, string domain)
    {
        _lastExternalId = TdmIdentity.ForNaturalKey(domain, "Customer", customerKey);
        await using var db = harness.NewBillingContext();
        var invoice = await db.Invoices.SingleAsync(i => i.InvoiceNumber == invoiceNumber, Ct);
        invoice.CustomerId.Should().Be(_lastExternalId);
    }

    [Then(@"a customer summary projection ""(.*)"" exists with that id")]
    public async Task ThenProjectionExists(string name)
    {
        await using var db = harness.NewBillingContext();
        var summary = await db.CustomerSummaries.SingleAsync(s => s.Name == name, Ct);
        summary.Id.Should().Be(_lastExternalId);
    }

    // ---------------------------------------------------------------- Determinism

    [Then(@"the generated customer emails match")]
    public void ThenEmailsMatch()
    {
        _firstRunEmail.Should().NotBeNull();
        _secondRunEmail.Should().Be(_firstRunEmail);
    }

    [Then(@"the generated customer emails differ")]
    public void ThenEmailsDiffer()
    {
        _firstRunEmail.Should().NotBeNull();
        _secondRunEmail.Should().NotBe(_firstRunEmail);
    }
}
