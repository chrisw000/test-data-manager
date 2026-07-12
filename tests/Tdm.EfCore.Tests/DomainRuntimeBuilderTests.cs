using Acme.Orders.Data.Persistence.Domain;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.EfCore.Tests;

public class DomainRuntimeBuilderTests
{
    [Fact]
    public async Task ModernProfile_StripsEntitySuffix_AndResolvesFromEfModel()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        Assert.Equal(["Customer", "Order", "Product"],
            runtime.Entities.Select(e => e.LogicalName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(runtime.TryResolveEntity("customers", out var customer, out _)); // plural + case tolerant
        Assert.Equal(typeof(CustomerEntity), customer!.ClrType);
    }

    [Fact]
    public async Task LegacyProfile_StripsModelSuffix()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();

        Assert.Contains("Account", runtime.Entities.Select(e => e.LogicalName));
        Assert.Contains("Invoice", runtime.Entities.Select(e => e.LogicalName));
        Assert.Contains("CustomerSummary", runtime.Entities.Select(e => e.LogicalName));
    }

    [Fact]
    public async Task KeyDetection_GuidDeterministic_IntIdentityDbGenerated()
    {
        await using var domains = new TestDomains();
        await using var orders = domains.BuildOrders();
        await using var billing = domains.BuildBilling();

        orders.TryResolveEntity("Customer", out var customer, out _);
        Assert.True(customer!.HasClientSettableGuidKey);
        Assert.Equal(IdStrategy.Deterministic, customer.IdStrategy);
        Assert.False(customer.KeyIsDbGenerated);

        billing.TryResolveEntity("Invoice", out var invoice, out _);
        Assert.True(invoice!.KeyIsDbGenerated);
        Assert.Equal(IdStrategy.DbGenerated, invoice.IdStrategy);
        Assert.Equal(typeof(int), invoice.KeyProperty!.PropertyType);
    }

    [Fact]
    public async Task NaturalKeys_ProfileDefaultAndPerEntityConfig()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        runtime.TryResolveEntity("Customer", out var customer, out _);
        Assert.Equal("Name", customer!.NaturalKeyProperty!.Name); // profile default

        runtime.TryResolveEntity("Product", out var product, out _);
        Assert.Equal("Sku", product!.NaturalKeyProperty!.Name); // entities config override
    }

    [Fact]
    public async Task NavigationNames_ExcludedFromScalarSnapshot()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        runtime.TryResolveEntity("Order", out var order, out _);
        Assert.Contains("Customer", order!.NavigationNames);
        Assert.DoesNotContain(order.ScalarProperties(), p => p.Name == "Customer");
    }

    [Fact]
    public async Task DescribeEntities_ReportsRepositoryFakerAndRoute()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var described = runtime.DescribeEntities().ToDictionary(i => i.LogicalName);

        // Well-known IRepository<T> shape.
        Assert.Contains("ICustomerRepository", described["Customer"].Repository);
        Assert.Equal("ICustomerRepository.Add", described["Customer"].PersistRoute);
        Assert.Equal("CustomerFaker", described["Customer"].FakerSource);

        // Duck-typed Add{Name} match.
        Assert.Equal("IOrderRepository.AddOrder", described["Order"].PersistRoute);
        Assert.Equal("auto", described["Order"].FakerSource);

        // No repository → DbContext fallback, warned.
        Assert.Null(described["Product"].Repository);
        Assert.StartsWith("DbContext", described["Product"].PersistRoute);
        Assert.Contains(runtime.Warnings, w => w.Contains("IProductRepository"));
        Assert.Contains(runtime.Warnings, w => w.Contains("OrderFaker"));
    }

    [Fact]
    public async Task DbContextOnlyDomain_RoutesEverythingThroughDbContext()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        Assert.All(runtime.DescribeEntities(), i => Assert.Equal("DbContext", i.PersistRoute));
    }

    [Fact]
    public async Task NoDbContextInAssemblies_Throws()
    {
        await using var domains = new TestDomains();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DomainRuntimeBuilder.Build(domains.Settings.Domains[0], domains.Settings,
                [typeof(DomainRuntimeBuilderTests).Assembly]));
        Assert.Contains("no public DbContext subclass", ex.Message);
    }

    [Fact]
    public async Task UnknownProvider_Throws()
    {
        await using var domains = new TestDomains();
        var domain = new DomainSettings
        {
            Name = "X", Provider = "Oracle", ConnectionString = "whatever", ConventionProfile = "modern",
        };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DomainRuntimeBuilder.Build(domain, domains.Settings, [typeof(Acme.Orders.Data.Persistence.OrdersDbContext).Assembly]));
        Assert.Contains("Oracle", ex.Message);
    }
}
