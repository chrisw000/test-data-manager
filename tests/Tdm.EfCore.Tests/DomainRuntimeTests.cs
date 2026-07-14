using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence.Domain;
using Acme.Orders.Domain.Catalog;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.EfCore.Tests;

public class DomainRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EntityDescriptor Entity(IDomainRuntime runtime, string name)
    {
        runtime.TryResolveEntity(name, out var descriptor, out var error).Should().BeTrue(error);
        return descriptor!;
    }

    // ---------------------------------------------------------------- Generation

    [Fact]
    public async Task Generate_ConventionFaker_DeterministicUnderSeed()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var customer = Entity(runtime, "Customer");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42, Ct);
        var first = (CustomerEntity)runtime.Generate(customer, out var source, []);
        await runtime.EndScenarioAsync(Ct);

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42, Ct);
        var second = (CustomerEntity)runtime.Generate(customer, out _, []);
        await runtime.EndScenarioAsync(Ct);

        source.Should().Be("CustomerFaker");
        second.Name.Should().Be(first.Name);
        second.Email.Should().Be(first.Email);

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 99, Ct);
        var different = (CustomerEntity)runtime.Generate(customer, out _, []);
        await runtime.EndScenarioAsync(Ct);
        different.Email.Should().NotBe(first.Email);
    }

    [Fact]
    public async Task Generate_AutoFaker_FillsScalars_SkipsKeyFkAndNavigation()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var order = Entity(runtime, "Order");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var generated = (OrderEntity)runtime.Generate(order, out var source, []);
        await runtime.EndScenarioAsync(Ct);

        source.Should().Be("auto");
        generated.OrderNumber.Should().NotBeNullOrEmpty();
        generated.OrderDate.Should().NotBe(default);
        generated.Id.Should().Be(Guid.Empty);          // key left to the identity contract
        generated.CustomerId.Should().Be(Guid.Empty);  // FK column skipped
        generated.Customer.Should().BeNull();          // navigation skipped
    }

    [Fact]
    public async Task Generate_AutoFaker_DeterministicUnderSeed()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var order = Entity(runtime, "Order");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 5, Ct);
        var first = (OrderEntity)runtime.Generate(order, out _, []);
        await runtime.EndScenarioAsync(Ct);
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 5, Ct);
        var second = (OrderEntity)runtime.Generate(order, out _, []);
        await runtime.EndScenarioAsync(Ct);

        second.OrderNumber.Should().Be(first.OrderNumber);
        second.Total.Should().Be(first.Total);
    }

    // ---------------------------------------------------------------- Persistence routing + CRUD

    [Fact]
    public async Task Create_RoutesViaWellKnownRepository_AndExercisesDomainBehaviour()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var customer = Entity(runtime, "Customer");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var instance = new CustomerEntity { Id = Guid.NewGuid(), Name = "Acme", Email = "a@b.c" };
        var outcome = await runtime.CreateAsync(customer, instance, ct: Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("ICustomerWriteRepository.Add");
        instance.CreatedUtc.Should().NotBe(default); // audit stamp from the repository

        await using var verify = domains.NewOrdersContext();
        (await verify.Customers.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Create_DuckTypedRepositoryRoute()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var customerEntity = new CustomerEntity { Id = Guid.NewGuid(), Name = "C" };
        await runtime.CreateAsync(Entity(runtime, "Customer"), customerEntity, ct: Ct);
        var order = new OrderEntity { Id = Guid.NewGuid(), OrderNumber = "ORD-1", CustomerId = customerEntity.Id };
        var outcome = await runtime.CreateAsync(Entity(runtime, "Order"), order, ct: Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("IOrderRepository.AddOrder");
    }

    [Fact]
    public async Task Create_NoRepository_FallsBackToDbContext()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var outcome = await runtime.CreateAsync(Entity(runtime, "Product"),
            new ProductEntity { Id = Guid.NewGuid(), Sku = "S-1", Name = "P" }, ct: Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("DbContext");
    }

    [Fact]
    public async Task FindUpdateDeleteCount_RoundTrip()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "S-1", Name = "P1", Category = "A" }, ct: Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "S-2", Name = "P2", Category = "A" }, ct: Ct);

        var found = (ProductEntity?)await runtime.FindByNaturalKeyAsync(product, "S-1", Ct);
        found.Should().NotBeNull();

        found!.Price = 99.99m;
        (await runtime.UpdateAsync(product, found, Ct)).Success.Should().BeTrue();

        var categoryA = new PropertyFilter(typeof(ProductEntity).GetProperty("Category")!, "A");
        (await runtime.CountAsync(product, [categoryA], Ct)).Should().Be(2);

        (await runtime.DeleteAsync(product, found, Ct)).Success.Should().BeTrue();
        (await runtime.CountAsync(product, [categoryA], Ct)).Should().Be(1);
        (await runtime.FindByNaturalKeyAsync(product, "S-1", Ct)).Should().BeNull();
        await runtime.EndScenarioAsync(Ct);
    }

    [Fact]
    public async Task FindByNaturalKey_MultipleMatches_Throws()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "DUP" }, ct: Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "DUP" }, ct: Ct);
        await FluentActions.Awaiting(() => runtime.FindByNaturalKeyAsync(product, "DUP", Ct))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unique*");
        await runtime.EndScenarioAsync(Ct);
    }

    [Fact]
    public async Task DeleteWhere_RemovesOnlyMatching()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "A", Category = "Del" }, ct: Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "B", Category = "Del" }, ct: Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "C", Category = "Keep" }, ct: Ct);

        var filter = new PropertyFilter(typeof(ProductEntity).GetProperty("Category")!, "Del");
        (await runtime.DeleteWhereAsync(product, [filter], Ct)).Should().Be(2);
        (await runtime.CountAsync(product, [], Ct)).Should().Be(1);
        await runtime.EndScenarioAsync(Ct);
    }

    [Fact]
    public async Task CreateBulk_ChunksAndPersistsAll()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var rows = Enumerable.Range(1, 25)
            .Select(i => (object)new ProductEntity { Id = Guid.NewGuid(), Sku = $"B-{i}", Category = "Bulk" })
            .ToList();
        var outcome = await runtime.CreateBulkAsync(product, rows, chunkSize: 10, Ct);
        await runtime.EndScenarioAsync(Ct);

        outcome.Success.Should().BeTrue(outcome.Error);
        outcome.Route.Should().Be("DbContext(bulk)");
        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(25);
    }

    [Fact]
    public async Task DbGeneratedIntKey_CapturedAfterInsert_AndDeletableById()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildBilling();
        var invoice = Entity(runtime, "Invoice");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        var account = new AccountModel { Id = Guid.NewGuid(), Name = "A1" };
        (await runtime.CreateAsync(Entity(runtime, "Account"), account, ct: Ct)).Success.Should().BeTrue();
        var row = new InvoiceModel { InvoiceNumber = "INV-1", Amount = 10, AccountId = account.Id };
        var outcome = await runtime.CreateAsync(invoice, row, ct: Ct);
        outcome.Success.Should().BeTrue(outcome.Error);
        row.Id.Should().BePositive(); // identity captured onto the instance

        (await runtime.DeleteByIdAsync("Invoice", row.Id.ToString(), Ct)).Should().BeTrue();
        (await runtime.DeleteByIdAsync("Invoice", row.Id.ToString(), Ct)).Should().BeFalse(); // already gone
        await runtime.EndScenarioAsync(Ct);
    }

    // ---------------------------------------------------------------- Lifecycles

    [Fact]
    public async Task Transactional_RollsBackAtScenarioEnd()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Transactional, seed: 1, Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "TX-1" }, ct: Ct);
        (await runtime.CountAsync(product, [], Ct)).Should().Be(1); // visible inside the transaction
        var close = await runtime.EndScenarioAsync(Ct);
        close.Error.Should().BeNull();

        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task TrackedTeardown_DeletesInReverseDependencyOrder()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();

        await runtime.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 1, Ct);
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Parent" };
        await runtime.CreateAsync(Entity(runtime, "Customer"), customer, ct: Ct);
        await runtime.CreateAsync(Entity(runtime, "Order"),
            new OrderEntity { Id = Guid.NewGuid(), OrderNumber = "O-1", CustomerId = customer.Id }, ct: Ct);

        var close = await runtime.EndScenarioAsync(Ct);
        close.Deleted.Should().Be(2);
        close.Orphaned.Should().BeEmpty();

        await using var verify = domains.NewOrdersContext();
        (await verify.Customers.CountAsync(Ct)).Should().Be(0);
        (await verify.Orders.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Persistent_RowsSurviveScenarioEnd()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct);
        await runtime.CreateAsync(product, new ProductEntity { Id = Guid.NewGuid(), Sku = "P-1" }, ct: Ct);
        await runtime.EndScenarioAsync(Ct);

        await using var verify = domains.NewOrdersContext();
        (await verify.Products.CountAsync(Ct)).Should().Be(1);
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
        runtime.TrySetReference(instance, order, "Customer", customer, null, customerId, out var error)
            .Should().BeTrue(error);
        instance.CustomerId.Should().Be(customerId);
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
        runtime.TrySetReference(instance, invoice, "Customer", null, null, externalId, out var error)
            .Should().BeTrue(error);
        instance.CustomerId.Should().Be(externalId);
    }

    [Fact]
    public async Task TrySetReference_NothingMatches_ReturnsFalseWithError()
    {
        await using var domains = new TestDomains();
        await using var runtime = domains.BuildOrders();
        var product = Entity(runtime, "Product");

        runtime.TrySetReference(new ProductEntity(), product, "Warehouse", null, null, Guid.NewGuid(), out var error)
            .Should().BeFalse();
        error.Should().Contain("Warehouse");
    }
}
