using Acme.Orders.Data.Persistence.Domain;
using AwesomeAssertions;
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

        runtime.Entities.Select(e => e.LogicalName).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("Customer", "Order", "Product");
        runtime.TryResolveEntity("customers", out var customer, out _).Should().BeTrue(); // plural + case tolerant
        customer!.ClrType.Should().Be(typeof(CustomerEntity));
    }

    [Fact]
    public async Task LegacyProfile_StripsModelSuffix()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();

        runtime.Entities.Select(e => e.LogicalName)
            .Should().Contain(["Account", "Invoice", "CustomerSummary"]);
    }

    [Fact]
    public async Task KeyDetection_GuidDeterministic_IntIdentityDbGenerated()
    {
        await using var domains = new TestDomains();
        await using var orders = domains.BuildOrders();
        await using var billing = domains.BuildBilling();

        orders.TryResolveEntity("Customer", out var customer, out _);
        customer!.HasClientSettableGuidKey.Should().BeTrue();
        customer.IdStrategy.Should().Be(IdStrategy.Deterministic);
        customer.KeyIsDbGenerated.Should().BeFalse();

        billing.TryResolveEntity("Invoice", out var invoice, out _);
        invoice!.KeyIsDbGenerated.Should().BeTrue();
        invoice.IdStrategy.Should().Be(IdStrategy.DbGenerated);
        invoice.KeyProperty!.PropertyType.Should().Be(typeof(int));
    }

    [Fact]
    public async Task NaturalKeys_ProfileDefaultAndPerEntityConfig()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        runtime.TryResolveEntity("Customer", out var customer, out _);
        customer!.NaturalKeyProperty!.Name.Should().Be("Name"); // profile default

        runtime.TryResolveEntity("Product", out var product, out _);
        product!.NaturalKeyProperty!.Name.Should().Be("Sku"); // entities config override
    }

    [Fact]
    public async Task NavigationNames_ExcludedFromScalarSnapshot()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        runtime.TryResolveEntity("Order", out var order, out _);
        order!.NavigationNames.Should().Contain("Customer");
        order.ScalarProperties().Should().NotContain(p => p.Name == "Customer");
    }

    [Fact]
    public async Task DescribeEntities_ReportsRepositoryFakerAndRoute()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var described = runtime.DescribeEntities().ToDictionary(i => i.LogicalName);

        // Well-known IRepository<T> shape.
        described["Customer"].Repository.Should().Contain("ICustomerRepository");
        described["Customer"].PersistRoute.Should().Be("ICustomerRepository.Add");
        described["Customer"].FakerSource.Should().Be("CustomerFaker");

        // Duck-typed Add{Name} match.
        described["Order"].PersistRoute.Should().Be("IOrderRepository.AddOrder");
        described["Order"].FakerSource.Should().Be("auto");

        // No repository → DbContext fallback, warned.
        described["Product"].Repository.Should().BeNull();
        described["Product"].PersistRoute.Should().StartWith("DbContext");
        runtime.Warnings.Should().Contain(w => w.Contains("IProductRepository"));
        runtime.Warnings.Should().Contain(w => w.Contains("OrderFaker"));
    }

    [Fact]
    public async Task DbContextOnlyDomain_RoutesEverythingThroughDbContext()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        runtime.DescribeEntities().Should().AllSatisfy(i => i.PersistRoute.Should().Be("DbContext"));
    }

    [Fact]
    public async Task NoDbContextInAssemblies_Throws()
    {
        await using var domains = new TestDomains();
        FluentActions.Invoking(() =>
                DomainRuntimeBuilder.Build(domains.Settings.Domains[0], domains.Settings,
                    [typeof(DomainRuntimeBuilderTests).Assembly]))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("no public DbContext subclass");
    }

    [Fact]
    public async Task UnknownProvider_Throws()
    {
        await using var domains = new TestDomains();
        var domain = new DomainSettings
        {
            Name = "X", Provider = "Oracle", ConnectionString = "whatever", ConventionProfile = "modern",
        };
        FluentActions.Invoking(() =>
                DomainRuntimeBuilder.Build(domain, domains.Settings, [typeof(Acme.Orders.Data.Persistence.OrdersDbContext).Assembly]))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("Oracle");
    }
}
