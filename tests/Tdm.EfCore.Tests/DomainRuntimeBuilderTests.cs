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
            .Should().Equal("Customer", "Order", "Product", "Warehouse");
        runtime.TryResolveEntity("customers", out var customer, out _).Should().BeTrue(); // plural + case tolerant
        customer!.ClrType.Should().Be(typeof(CustomerEntity));

        // ProductEntity lives outside the conventional namespace ("defined elsewhere in the
        // domain layer") — EF-model-first discovery finds it regardless.
        runtime.TryResolveEntity("Product", out var product, out _).Should().BeTrue();
        product!.ClrType.Should().Be(typeof(Acme.Orders.Domain.Catalog.ProductEntity));
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
    public async Task DescribeEntities_ReportsSplitRepositoriesFakerAndRoute()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var described = runtime.DescribeEntities().ToDictionary(i => i.LogicalName);

        // Split read/write pair, no generic marker interface — probed by pattern (ADR-0001).
        described["Customer"].Repository.Should().Be("ICustomerWriteRepository → CustomerWriteRepository");
        described["Customer"].ReadRepository.Should().Be("ICustomerReadRepository → CustomerReadRepository");
        described["Customer"].PersistRoute.Should().Be("ICustomerWriteRepository.Add");
        described["Customer"].FakerSource.Should().Be("CustomerFaker");

        // Plain I{Name}Repository fallback pattern + duck-typed Add{Name} match; the same
        // interface doubles as the read repo via the read-pattern fallback.
        described["Order"].PersistRoute.Should().Be("IOrderRepository.AddOrder");
        described["Order"].ReadRepository.Should().Be("IOrderRepository → OrderRepository");
        described["Order"].FakerSource.Should().Be("auto");

        // Read repository without a write repository → DbContext persist route.
        described["Product"].Repository.Should().BeNull();
        described["Product"].ReadRepository.Should().Be("IProductReadRepository → ProductReadRepository");
        described["Product"].PersistRoute.Should().StartWith("DbContext");
        runtime.Warnings.Should().Contain(w => w.Contains("IProductRepository") && w.Contains("exempted"));
        runtime.Warnings.Should().Contain(w => w.Contains("OrderFaker"));
    }

    // ---------------------------------------------------------------- Write-repository policy (ADR-0001)

    [Fact]
    public async Task Policy_ExemptedEntity_NoViolation()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        runtime.PolicyViolations.Should().BeEmpty();
    }

    [Fact]
    public async Task Policy_RequiredWriteRepositoryMissing_RecordsViolation()
    {
        await using var domains = new TestDomains();
        // Remove Product's exemption: the modern profile requires a write repository.
        domains.Settings.Entities["Product"] = new EntitySettings { NaturalKey = "Sku" };
        await using var runtime = domains.BuildOrders();

        var violation = runtime.PolicyViolations.Should().ContainSingle().Subject;
        violation.Should().Contain("Orders.Product")
            .And.Contain("IProductWriteRepository")  // names the probed patterns
            .And.Contain("requireRepository");       // and the way out
    }

    [Fact]
    public async Task Policy_ExplicitWriteRepositoryName_WinsOverPatterns()
    {
        await using var domains = new TestDomains();
        domains.Settings.Entities["Customer"] = new EntitySettings { WriteRepository = "ICustomerWriteRepository" };
        await using var explicitHit = domains.BuildOrders();
        explicitHit.DescribeEntities().Single(i => i.LogicalName == "Customer")
            .PersistRoute.Should().Be("ICustomerWriteRepository.Add");

        domains.Settings.Entities["Customer"] = new EntitySettings { WriteRepository = "INoSuchRepository" };
        await using var explicitMiss = domains.BuildOrders();
        explicitMiss.PolicyViolations.Should().Contain(v =>
            v.Contains("Orders.Customer") && v.Contains("INoSuchRepository"));
    }

    [Fact]
    public async Task Policy_DbContextOnlyDomain_IsADeclaredChoice_NoViolations()
    {
        await using var domains = new TestDomains(ordersPersistence: PersistenceMode.DbContextOnly);
        await using var runtime = domains.BuildOrders();
        runtime.PolicyViolations.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- IEntityTypeConfiguration cross-check

    [Fact]
    public async Task ConfiguredButUnmappedEntity_DiscoveredWithCrossCheckWarning()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        // WarehouseEntityConfiguration exists but was never applied in OrdersDbContext.
        runtime.TryResolveEntity("Warehouse", out var warehouse, out _).Should().BeTrue();
        warehouse!.ClrType.Name.Should().Be("WarehouseEntity");
        runtime.Warnings.Should().Contain(w =>
            w.Contains("WarehouseEntityConfiguration") && w.Contains("not mapped"));

        // Generation-only: persisting reports a clear error rather than exploding.
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, TestContext.Current.CancellationToken);
        var outcome = await runtime.CreateAsync(warehouse, new Acme.Orders.Data.Persistence.Domain.WarehouseEntity(),
            ct: TestContext.Current.CancellationToken);
        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("not mapped");
        await runtime.EndScenarioAsync(TestContext.Current.CancellationToken);
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
