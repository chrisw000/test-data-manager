using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.EfCore;
using Tdm.Tests.Matrix;
using Xunit;

namespace Tdm.Integration.Tests.Support;

/// <summary>
/// One isolated TDM environment per Reqnroll scenario: fresh databases in the matrix provider
/// (W3-P3 — temp SQLite by default; SqlServer/PostgreSql containers under TDM_TEST_PROVIDER)
/// for both sample domains, in-code settings, real DomainRuntimes, real TdmEngine. Reqnroll's
/// context injection creates and disposes one instance per scenario.
/// </summary>
public sealed class TdmHarness : IDisposable
{
    private readonly TestDatabases _databases = ProviderMatrix.CreateDatabases("orders", "billing");
    private readonly List<IDomainRuntime> _runtimes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TdmHarness()
    {
        OrdersConnectionString = _databases["orders"];
        BillingConnectionString = _databases["billing"];
        Settings = BuildSettings(OrdersConnectionString, BillingConnectionString, _databases.Provider);
    }

    public TdmSettings Settings { get; }
    public string OrdersConnectionString { get; }
    public string BillingConnectionString { get; }
    public RunManifest? Manifest { get; private set; }

    public static TdmSettings BuildSettings(string ordersConnectionString, string billingConnectionString,
        string provider = "Sqlite")
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
                    Name = "Orders", Provider = provider, ConnectionString = ordersConnectionString,
                    ConventionProfile = "modern", Persistence = PersistenceMode.RepositoryFirst, EnsureCreated = true,
                },
                new DomainSettings
                {
                    Name = "Billing", Provider = provider, ConnectionString = billingConnectionString,
                    ConventionProfile = "legacy", Persistence = PersistenceMode.DbContextOnly, EnsureCreated = true,
                },
            ],
            Entities =
            {
                // Product has no write repository by design — exempted from the policy (ADR-0001).
                ["Product"] = new EntitySettings { NaturalKey = "Sku", RequireRepository = false },
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

    // Verification contexts go through the provider registry, same as the runtime's own.
    public OrdersDbContext NewOrdersContext() => new(
        (DbContextOptions<OrdersDbContext>)DbContextActivator.BuildOptions(typeof(OrdersDbContext), Settings.Domains[0]));

    public BillingDbContext NewBillingContext() => new(
        (DbContextOptions<BillingDbContext>)DbContextActivator.BuildOptions(typeof(BillingDbContext), Settings.Domains[1]));

    public RunManifest RequireManifest() =>
        Manifest ?? throw new InvalidOperationException("No TDM run has been executed yet in this scenario.");

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _runtimes.Clear();
        _databases.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
