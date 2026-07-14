using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.EfCore;
using Xunit;

namespace Tdm.Integration.Tests.Support;

/// <summary>
/// One isolated TDM environment per Reqnroll scenario: fresh temp SQLite databases for both
/// sample domains, in-code settings, real DomainRuntimes, real TdmEngine. Reqnroll's context
/// injection creates and disposes one instance per scenario.
/// </summary>
public sealed class TdmHarness : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tdm-integration-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDomainRuntime> _runtimes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TdmHarness()
    {
        Directory.CreateDirectory(_directory);
        OrdersConnectionString = $"Data Source={Path.Combine(_directory, "orders.db")}";
        BillingConnectionString = $"Data Source={Path.Combine(_directory, "billing.db")}";
        Settings = BuildSettings(OrdersConnectionString, BillingConnectionString);
    }

    public TdmSettings Settings { get; }
    public string OrdersConnectionString { get; }
    public string BillingConnectionString { get; }
    public RunManifest? Manifest { get; private set; }

    public static TdmSettings BuildSettings(string ordersConnectionString, string billingConnectionString)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings
            {
                Name = "integration",
                FailurePolicy = FailurePolicy.BestEffort,
                Lifecycle = LifecycleMode.Persistent,
                DefaultSeed = 1,
            },
            Domains =
            [
                new DomainSettings
                {
                    Name = "Orders", Provider = "Sqlite", ConnectionString = ordersConnectionString,
                    ConventionProfile = "modern", Persistence = PersistenceMode.RepositoryFirst, EnsureCreated = true,
                },
                new DomainSettings
                {
                    Name = "Billing", Provider = "Sqlite", ConnectionString = billingConnectionString,
                    ConventionProfile = "legacy", Persistence = PersistenceMode.DbContextOnly, EnsureCreated = true,
                },
            ],
            Entities =
            {
                ["Product"] = new EntitySettings { NaturalKey = "Sku" },
                ["Order"] = new EntitySettings { NaturalKey = "OrderNumber" },
                ["Invoice"] = new EntitySettings { NaturalKey = "InvoiceNumber" },
                ["CustomerSummary"] = new EntitySettings { NaturalKey = "Name" },
                ["Customer"] = new EntitySettings
                {
                    ExternalBehavior = ExternalBehavior.Projection,
                    ProjectionEntity = "CustomerSummary",
                },
            },
        };
        settings.ApplyDefaults();
        return settings;
    }

    /// <summary>Runs the engine over TDM feature text. Runtimes (and databases) persist across
    /// calls within the same harness, so multi-run scenarios exercise DB-backed resolution.</summary>
    public async Task<RunManifest> RunAsync(string tdmFeatureText, bool dryRun = false)
    {
        if (_runtimes.Count == 0)
        {
            _runtimes.Add(DomainRuntimeBuilder.Build(Settings.Domains[0], Settings, [typeof(OrdersDbContext).Assembly]));
            _runtimes.Add(DomainRuntimeBuilder.Build(Settings.Domains[1], Settings, [typeof(BillingDbContext).Assembly]));
        }
        var plan = new SeedingPlan { Features = { new GherkinPlanParser().ParseText(tdmFeatureText) } };
        Manifest = await new TdmEngine(Settings, _runtimes).RunAsync(plan, dryRun, Ct);
        return Manifest;
    }

    /// <summary>Runs with an externally built runtime set (e.g. plugin-loaded assemblies).</summary>
    public async Task<RunManifest> RunWithRuntimesAsync(string tdmFeatureText, IReadOnlyList<IDomainRuntime> runtimes)
    {
        var plan = new SeedingPlan { Features = { new GherkinPlanParser().ParseText(tdmFeatureText) } };
        Manifest = await new TdmEngine(Settings, runtimes).RunAsync(plan, ct: Ct);
        return Manifest;
    }

    public OrdersDbContext NewOrdersContext() => new(
        new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(OrdersConnectionString).Options);

    public BillingDbContext NewBillingContext() => new(
        new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(BillingConnectionString).Options);

    public RunManifest RequireManifest() =>
        Manifest ?? throw new InvalidOperationException("No TDM run has been executed yet in this scenario.");

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtimes.Clear();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
