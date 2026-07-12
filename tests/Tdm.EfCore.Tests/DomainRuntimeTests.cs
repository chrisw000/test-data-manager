using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence.Domain;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.EfCore.Tests;

public class DomainRuntimeTests
{
    private static EntityDescriptor Entity(IDomainRuntime runtime, string name)
    {
        Assert.True(runtime.TryResolveEntity(name, out var descriptor, out var error), error);
        return descriptor!;
    }

    // ---------------------------------------------------------------- Generation

    [Fact]
    public async Task Generate_ConventionFaker_DeterministicUnderSeed()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var customer = Entity(runtime, "Customer");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42);
        var first = (CustomerEntity)runtime.Generate(customer, out var source, []);
        await runtime.EndScenarioAsync();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42);
        var second = (CustomerEntity)runtime.Generate(customer, out _, []);
        await runtime.EndScenarioAsync();

        Assert.Equal("CustomerFaker", source);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Email, second.Email);

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 99);
        var different = (CustomerEntity)runtime.Generate(customer, out _, []);
        await runtime.EndScenarioAsync();
        Assert.NotEqual(first.Email, different.Email);
    }

    [Fact]
    public async Task Generate_AutoFaker_FillsScalars_SkipsKeyFkAndNavigation()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var order = Entity(runtime, "Order");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var generated = (OrderEntity)runtime.Generate(order, out var source, []);
        await runtime.EndScenarioAsync();

        Assert.Equal("auto", source);
        Assert.False(string.IsNullOrEmpty(generated.OrderNumber));
        Assert.NotEqual(default, generated.OrderDate);
        Assert.Equal(Guid.Empty, generated.Id);          // key left to the identity contract
        Assert.Equal(Guid.Empty, generated.CustomerId);  // FK column skipped
        Assert.Null(generated.Customer);                 // navigation skipped
    }

    [Fact]
    public async Task Generate_AutoFaker_DeterministicUnderSeed()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var order = Entity(runtime, "Order");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 5);
        var first = (OrderEntity)runtime.Generate(order, out _, []);
        await runtime.EndScenarioAsync();
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 5);
        var second = (OrderEntity)runtime.Generate(order, out _, []);
        await runtime.EndScenarioAsync();

        Assert.Equal(first.OrderNumber, second.OrderNumber);
        Assert.Equal(first.Total, second.Total);
    }

    // ---------------------------------------------------------------- Persistence routing + CRUD

    [Fact]
    public async Task Create_RoutesViaWellKnownRepository_AndExercisesDomainBehaviour()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var customer = Entity(runtime, "Customer");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var instance = new CustomerEntity { Id = Guid.NewGuid(), Name = "Acme", Email = "a@b.c" };
        var outcome = await runtime.CreateAsync(customer, instance);
        await runtime.EndScenarioAsync();

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("ICustomerRepository.Add", outcome.Route);
        Assert.NotEqual(default, instance.CreatedUtc); // audit stamp from the repository

        await using var verify = domains.NewOrdersContext();
        Assert.Equal(1, await verify.Customers.CountAsync());
    }

    [Fact]
    public async Task Create_DuckTypedRepositoryRoute()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var customerEntity = new CustomerEntity { Id = Guid.NewGuid(), Name = "C" };
        await runtime.CreateAsync(Entity(runtime, "Customer"), customerEntity);
        var order = new OrderEntity { Id = Guid.NewGuid(), OrderNumber = "ORD-1", CustomerId = customerEntity.Id };
        var outcome = await runtime.CreateAsync(Entity(runtime, "Order"), order);
        await runtime.EndScenarioAsync();

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("IOrderRepository.AddOrder", outcome.Route);
    }

    [Fact]
    public async Task Create_NoRepository_FallsBackToDbContext()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var outcome = await runtime.CreateAsync(Entity(runtime, "Product"),
            new ProductEntity { Id = Guid.NewGuid(), Sku = "S-1", Name = "P" });
        await runtime.EndScenarioAsync();

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("DbContext", outcome.Route);
    }

    [Fact]
    public async Task FindUpdateDeleteCount_RoundTrip()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "S-1", Name = "P1", Category = "A" });
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "S-2", Name = "P2", Category = "A" });

        var found = (ProductEntity?)await runtime.FindByNaturalKeyAsync(product, "S-1");
        Assert.NotNull(found);

        found!.Price = 99.99m;
        Assert.True((await runtime.UpdateAsync(product, found)).Success);

        var categoryA = new PropertyFilter(typeof(ProductEntity).GetProperty("Category")!, "A");
        Assert.Equal(2, await runtime.CountAsync(product, [categoryA]));

        Assert.True((await runtime.DeleteAsync(product, found)).Success);
        Assert.Equal(1, await runtime.CountAsync(product, [categoryA]));
        Assert.Null(await runtime.FindByNaturalKeyAsync(product, "S-1"));
        await runtime.EndScenarioAsync();
    }

    [Fact]
    public async Task FindByNaturalKey_MultipleMatches_Throws()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "DUP" });
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "DUP" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.FindByNaturalKeyAsync(product, "DUP"));
        Assert.Contains("unique", ex.Message);
        await runtime.EndScenarioAsync();
    }

    [Fact]
    public async Task DeleteWhere_RemovesOnlyMatching()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "A", Category = "Del" });
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "B", Category = "Del" });
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "C", Category = "Keep" });

        var filter = new PropertyFilter(typeof(ProductEntity).GetProperty("Category")!, "Del");
        Assert.Equal(2, await runtime.DeleteWhereAsync(product, [filter]));
        Assert.Equal(1, await runtime.CountAsync(product, []));
        await runtime.EndScenarioAsync();
    }

    [Fact]
    public async Task CreateBulk_ChunksAndPersistsAll()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var rows = Enumerable.Range(1, 25)
            .Select(i => (object)new ProductEntity { Id = Guid.NewGuid(), Sku = $"B-{i}", Category = "Bulk" })
            .ToList();
        var outcome = await runtime.CreateBulkAsync(product, rows, chunkSize: 10);
        await runtime.EndScenarioAsync();

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("DbContext(bulk)", outcome.Route);
        await using var verify = domains.NewOrdersContext();
        Assert.Equal(25, await verify.Products.CountAsync());
    }

    [Fact]
    public async Task DbGeneratedIntKey_CapturedAfterInsert_AndDeletableById()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        var invoice = Entity(runtime, "Invoice");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        var account = new AccountModel { Id = Guid.NewGuid(), Name = "A1" };
        Assert.True((await runtime.CreateAsync(Entity(runtime, "Account"), account)).Success);
        var row = new InvoiceModel { InvoiceNumber = "INV-1", Amount = 10, AccountId = account.Id };
        var outcome = await runtime.CreateAsync(invoice, row);
        Assert.True(outcome.Success, outcome.Error);
        Assert.True(row.Id > 0); // identity captured onto the instance

        Assert.True(await runtime.DeleteByIdAsync("Invoice", row.Id.ToString()));
        Assert.False(await runtime.DeleteByIdAsync("Invoice", row.Id.ToString())); // already gone
        await runtime.EndScenarioAsync();
    }

    // ---------------------------------------------------------------- Lifecycles

    [Fact]
    public async Task Transactional_RollsBackAtScenarioEnd()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Transactional, seed: 1);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "TX-1" });
        Assert.Equal(1, await runtime.CountAsync(product, [])); // visible inside the transaction
        var close = await runtime.EndScenarioAsync();
        Assert.Null(close.Error);

        await using var verify = domains.NewOrdersContext();
        Assert.Equal(0, await verify.Products.CountAsync());
    }

    [Fact]
    public async Task TrackedTeardown_DeletesInReverseDependencyOrder()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 1);
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Parent" };
        await runtime.CreateAsync(Entity(runtime, "Customer"), customer);
        await runtime.CreateAsync(Entity(runtime, "Order"),
            new OrderEntity { Id = Guid.NewGuid(), OrderNumber = "O-1", CustomerId = customer.Id });

        var close = await runtime.EndScenarioAsync();
        Assert.Equal(2, close.Deleted);
        Assert.Empty(close.Orphaned);

        await using var verify = domains.NewOrdersContext();
        Assert.Equal(0, await verify.Customers.CountAsync());
        Assert.Equal(0, await verify.Orders.CountAsync());
    }

    [Fact]
    public async Task Persistent_RowsSurviveScenarioEnd()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "P-1" });
        await runtime.EndScenarioAsync();

        await using var verify = domains.NewOrdersContext();
        Assert.Equal(1, await verify.Products.CountAsync());
    }

    // ---------------------------------------------------------------- References

    [Fact]
    public async Task TrySetReference_MappedForeignKey_SetsFkColumn()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var order = Entity(runtime, "Order");
        var customer = Entity(runtime, "Customer");

        var customerId = Guid.NewGuid();
        var instance = new OrderEntity();
        Assert.True(runtime.TrySetReference(instance, order, "Customer", customer, null, customerId, out var error), error);
        Assert.Equal(customerId, instance.CustomerId);
    }

    [Fact]
    public async Task TrySetReference_NameConventionFallback_ForExternalPrincipal()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        var invoice = Entity(runtime, "Invoice");

        // "Customer" is owned by another domain: no mapped FK exists on InvoiceModel,
        // so the {Entity}Id convention property carries the identity-contract GUID.
        var externalId = Guid.NewGuid();
        var instance = new InvoiceModel();
        Assert.True(runtime.TrySetReference(instance, invoice, "Customer", null, null, externalId, out var error), error);
        Assert.Equal(externalId, instance.CustomerId);
    }

    [Fact]
    public async Task TrySetReference_NothingMatches_ReturnsFalseWithError()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        Assert.False(runtime.TrySetReference(new ProductEntity(), product, "Warehouse", null, null, Guid.NewGuid(), out var error));
        Assert.Contains("Warehouse", error);
    }
}
