using Acme.Orders.Data.Persistence;
using Acme.Orders.Domain.Catalog;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Settings;
using Tdm.EfCore.Bulk;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>
/// Provider-native bulk insert (W3-D3): the matrix provider's native inserter end to end
/// (SQLite multi-row batches by default; SqlBulkCopy / Npgsql binary COPY under the W3-P3
/// container matrix), strategy fallbacks, transaction enlistment, set-based teardown, and
/// the SqlBulkCopy column mapping proven offline against a SQL Server-optioned EF model.
/// </summary>
public class BulkInsertEfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EntityDescriptor Entity(IDomainRuntime runtime, string name)
    {
        runtime.TryResolveEntity(name, out var descriptor, out var error).Should().BeTrue(error);
        return descriptor!;
    }

    private static List<object> ProductRows(int count, string category = "Bulk") =>
        [.. Enumerable.Range(1, count).Select(i => (object)new ProductEntity
        {
            Id = Guid.NewGuid(),
            Sku = $"BLK-{category}-{i:D5}",
            Name = $"Bulk product {i}",
            Price = 10.50m + i,
            Category = category,
            Discontinued = i % 7 == 0,
        })];

    [Fact]
    public async Task ProviderStrategy_UsesNativeRoute_AndValuesRoundTrip()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var outcome = await runtime.CreateBulkAsync(product, ProductRows(150),
            new BulkPersistOptions(ChunkSize: 50, BulkStrategy.Provider), Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be(Tdm.Tests.Matrix.ProviderMatrix.ExpectedBulkRoute);

        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(150);
        var row = await verify.Products.SingleAsync(p => p.Sku == "BLK-Bulk-00003", Ct);
        (row.Name, row.Price, row.Category, row.Discontinued).Should()
            .Be(("Bulk product 3", 13.50m, "Bulk", false));
    }

    [Fact]
    public async Task ProviderStrategy_TrackedTeardown_RemovesAllRowsSetBased()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 1, Ct);
        var outcome = await runtime.CreateBulkAsync(product, ProductRows(120, "Teardown"),
            new BulkPersistOptions(ChunkSize: 40, BulkStrategy.Provider), Ct);
        outcome.Success.Should().BeTrue(outcome.Error);

        var close = await runtime.EndScenarioAsync(Ct);
        close.Deleted.Should().Be(120);
        close.Orphaned.Should().BeEmpty();

        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ProviderStrategy_Transactional_RollsBackWithTheScenario()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Transactional, seed: 1, Ct);
        var outcome = await runtime.CreateBulkAsync(product, ProductRows(30, "Tx"),
            new BulkPersistOptions(ChunkSize: 10, BulkStrategy.Provider), Ct);
        outcome.Success.Should().BeTrue(outcome.Error);
        await runtime.EndScenarioAsync(Ct); // Transactional = rollback

        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(0, "the inserter must enlist in the scenario transaction");
    }

    [Fact]
    public async Task EfCoreStrategy_ForcesPortablePath()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var outcome = await runtime.CreateBulkAsync(product, ProductRows(20, "Ef"),
            new BulkPersistOptions(ChunkSize: 10, BulkStrategy.EfCore), Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("DbContext(bulk)");
    }

    [Fact]
    public async Task DbGeneratedKey_FallsBackToEfPath_SoKeysPropagateBack()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        var account = Entity(runtime, "Account");
        var invoice = Entity(runtime, "Invoice");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var accountRow = new Acme.Billing.Data.Infrastructure.AccountModel { Id = Guid.NewGuid(), Name = "Bulk A/C" };
        (await runtime.CreateAsync(account, accountRow, ct: Ct)).Success.Should().BeTrue();

        var invoices = Enumerable.Range(1, 8).Select(i => (object)new Acme.Billing.Data.Infrastructure.InvoiceModel
        {
            InvoiceNumber = $"BLK-INV-{i}",
            AccountId = accountRow.Id,
            Amount = 100m + i,
        }).ToList();
        var outcome = await runtime.CreateBulkAsync(invoice, invoices,
            new BulkPersistOptions(ChunkSize: 4, BulkStrategy.Provider), Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("DbContext(bulk)", "identity keys cannot round-trip through a provider bulk path");
        invoices.Cast<Acme.Billing.Data.Infrastructure.InvoiceModel>()
            .Should().OnlyContain(i => i.Id != 0, "the EF path propagates generated keys back");
    }

    // ------------------------------------------------- SqlBulkCopy mapping, offline

    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType SqlServerEntityType<T>()
    {
        // Model building is offline — no connection is opened (same property `tdm validate` relies on).
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseSqlServer("Server=offline;Database=none;Trusted_Connection=True;")
            .Options;
        using var ctx = new OrdersDbContext(options);
        return ctx.Model.FindEntityType(typeof(T))!;
    }

    [Fact]
    public void SqlServerModel_MapsInsertableColumns_ThroughValueConverters()
    {
        var entityType = SqlServerEntityType<ProductEntity>();
        BulkColumns.TryMap(entityType, out var map, out var reason).Should().BeTrue(reason);

        map!.TableName.Should().NotBeNullOrEmpty();
        map.Columns.Select(c => c.Property.Name).Should()
            .BeEquivalentTo(["Id", "Sku", "Name", "Price", "Category", "Discontinued"]);

        var rows = ProductRows(3);
        var table = SqlServerBulkInserter.BuildTable(map, rows);
        table.Rows.Count.Should().Be(3);
        table.Columns.Count.Should().Be(map.Columns.Count);
        // Provider values: Guid keys stay Guids on SQL Server; decimals stay decimal.
        var idColumn = map.Columns.Single(c => c.Property.Name == "Id");
        table.Rows[0][idColumn.ColumnName].Should().Be(((ProductEntity)rows[0]).Id);
        var priceColumn = map.Columns.Single(c => c.Property.Name == "Price");
        table.Rows[1][priceColumn.ColumnName].Should().Be(((ProductEntity)rows[1]).Price);
    }

    [Fact]
    public void SqlServerModel_EnumColumn_ConvertsToProviderType()
    {
        var entityType = SqlServerEntityType<Acme.Orders.Data.Persistence.Domain.OrderEntity>();
        BulkColumns.TryMap(entityType, out var map, out var reason).Should().BeTrue(reason);

        // Status is an enum — the EF converter must project it to the store type, and
        // navigations (Customer) must not appear as columns.
        map!.Columns.Select(c => c.Property.Name).Should().NotContain("Customer");
        var status = map.Columns.Single(c => c.Property.Name == "Status");
        var value = status.ProviderValue(new Acme.Orders.Data.Persistence.Domain.OrderEntity
        {
            Status = Acme.Orders.Data.Persistence.Domain.OrderStatus.Shipped,
        });
        value.Should().NotBeNull();
        value!.GetType().Should().Be(status.ProviderClrType);
    }

    [Fact]
    public void DbGeneratedIdentityKey_IsRejected_WithReason()
    {
        var options = new DbContextOptionsBuilder<Acme.Billing.Data.Infrastructure.BillingDbContext>()
            .UseSqlServer("Server=offline;Database=none;Trusted_Connection=True;")
            .Options;
        using var ctx = new Acme.Billing.Data.Infrastructure.BillingDbContext(options);
        var entityType = ctx.Model.FindEntityType(typeof(Acme.Billing.Data.Infrastructure.InvoiceModel))!;

        BulkColumns.TryMap(entityType, out var map, out var reason).Should().BeFalse();
        map.Should().BeNull();
        reason.Should().Contain("store-generated");
    }
}
