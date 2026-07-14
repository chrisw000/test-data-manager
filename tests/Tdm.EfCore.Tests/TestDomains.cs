using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Settings;
using Tdm.EfCore;

namespace Tdm.EfCore.Tests;

/// <summary>Per-test fixture: unique temp SQLite databases + runtimes over the sample domains.</summary>
public sealed class TestDomains : IAsyncDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tdm-efcore-tests", Guid.NewGuid().ToString("N"));

    public TestDomains(PersistenceMode ordersPersistence = PersistenceMode.RepositoryFirst)
    {
        Directory.CreateDirectory(_directory);
        OrdersConnectionString = $"Data Source={Path.Combine(_directory, "orders.db")}";
        BillingConnectionString = $"Data Source={Path.Combine(_directory, "billing.db")}";

        Settings = new TdmSettings
        {
            Run = new RunSettings { Name = "efcore-tests" },
            Domains =
            [
                new DomainSettings
                {
                    Name = "Orders", Provider = "Sqlite", ConnectionString = OrdersConnectionString,
                    ConventionProfile = "modern", Persistence = ordersPersistence, EnsureCreated = true,
                },
                new DomainSettings
                {
                    Name = "Billing", Provider = "Sqlite", ConnectionString = BillingConnectionString,
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

    public OrdersDbContext NewOrdersContext() => new(
        new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(OrdersConnectionString).Options);

    public BillingDbContext NewBillingContext() => new(
        new DbContextOptionsBuilder<BillingDbContext>().UseSqlite(BillingConnectionString).Options);

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
