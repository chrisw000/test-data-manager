using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Settings;
using Tdm.EfCore;
using Tdm.Tests.Matrix;

namespace Tdm.EfCore.Tests;

/// <summary>Per-test fixture: unique databases in the matrix provider (W3-P3 — SQLite temp
/// files by default; SqlServer/PostgreSql containers under TDM_TEST_PROVIDER) + runtimes over
/// the sample domains.</summary>
public sealed class TestDomains : IAsyncDisposable
{
    private readonly TestDatabases _databases = ProviderMatrix.CreateDatabases("orders", "billing");

    public TestDomains(PersistenceMode ordersPersistence = PersistenceMode.RepositoryFirst)
    {
        OrdersConnectionString = _databases["orders"];
        BillingConnectionString = _databases["billing"];

        Settings = new TdmSettings
        {
            Run = new RunSettings { Name = "efcore-tests" },
            Domains =
            [
                new DomainSettings
                {
                    Name = "Orders", Provider = _databases.Provider, ConnectionString = OrdersConnectionString,
                    ConventionProfile = "modern", Persistence = ordersPersistence, EnsureCreated = true,
                },
                new DomainSettings
                {
                    Name = "Billing", Provider = _databases.Provider, ConnectionString = BillingConnectionString,
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
            },
        };
        Settings.ApplyDefaults();
    }

    public TdmSettings Settings { get; }
    public string OrdersConnectionString { get; }
    public string BillingConnectionString { get; }

    public DomainRuntime BuildOrders() =>
        DomainRuntimeBuilder.Build(Settings.Domains[0], Settings, [typeof(OrdersDbContext).Assembly]);

    public DomainRuntime BuildBilling() =>
        DomainRuntimeBuilder.Build(Settings.Domains[1], Settings, [typeof(BillingDbContext).Assembly]);

    // Verification contexts go through the provider registry, same as the runtime's own.
    public OrdersDbContext NewOrdersContext() => new(
        (DbContextOptions<OrdersDbContext>)DbContextActivator.BuildOptions(typeof(OrdersDbContext), Settings.Domains[0]));

    public BillingDbContext NewBillingContext() => new(
        (DbContextOptions<BillingDbContext>)DbContextActivator.BuildOptions(typeof(BillingDbContext), Settings.Domains[1]));

    public ValueTask DisposeAsync() => _databases.DisposeAsync();
}
