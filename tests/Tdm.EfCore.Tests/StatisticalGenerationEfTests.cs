using Acme.Orders.Data.Persistence;
using Acme.Orders.Data.Persistence.Domain;
using Acme.Orders.Domain.Catalog;
using AwesomeAssertions;
using Bogus;
using Tdm.Core.Generation;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>Discovered from this test assembly like a plugin-shipped generator would be:
/// extends the auto-faker table for Order.OrderNumber with code (W4-D4).</summary>
public sealed class TestOrderNumberGenerator : IValueGeneratorPlugin
{
    public string Name => "TestOrderNumbers";

    public bool Matches(ValueGenerationContext context) =>
        context.Entity == "Order" && context.Property.Name == nameof(OrderEntity.OrderNumber);

    public object? Generate(ValueGenerationContext context, Randomizer random) =>
        $"ORD-PLUGIN-{random.Int(1000, 9999)}";
}

public class StatisticalGenerationEfTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestDomains _domains = new();

    public async ValueTask DisposeAsync() => await _domains.DisposeAsync();

    /// <summary>The wave-4 acceptance criterion (§5): a weighted Status over 10k generated
    /// orders lands within 2% of configured weights — and identically across two same-seed runs.</summary>
    [Fact]
    public async Task WeightedStatus_Over10kOrders_Within2Percent_AndSeedIdentical()
    {
        _domains.Settings.Entities["Order"].Properties["Status"] = new PropertyGenerationSettings
        {
            Weights = new Dictionary<string, double> { ["Pending"] = 0.6, ["Shipped"] = 0.3, ["Cancelled"] = 0.1 },
        };
        _domains.Settings.Entities["Order"].Properties["Total"] = new PropertyGenerationSettings
        {
            Distribution = "lognormal", Mean = 120, Sigma = 1.2,
        };
        await using var runtime = _domains.BuildOrders();
        runtime.TryResolveEntity("Order", out var order, out _).Should().BeTrue();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42, Ct);
        var counts = new Dictionary<OrderStatus, int>();
        var firstRun = new List<(OrderStatus, decimal)>();
        string? fakerSource = null;
        for (var i = 0; i < 10_000; i++)
        {
            var instance = (OrderEntity)runtime.Generate(order!, out fakerSource, []);
            counts[instance.Status] = counts.GetValueOrDefault(instance.Status) + 1;
            instance.Total.Should().BePositive();
            if (i < 500) firstRun.Add((instance.Status, instance.Total));
        }
        await runtime.EndScenarioAsync(Ct);

        fakerSource.Should().Be("auto+distributions");
        (counts[OrderStatus.Pending] / 10_000d).Should().BeApproximately(0.6, 0.02);
        (counts[OrderStatus.Shipped] / 10_000d).Should().BeApproximately(0.3, 0.02);
        (counts[OrderStatus.Cancelled] / 10_000d).Should().BeApproximately(0.1, 0.02);
        counts.Keys.Should().BeEquivalentTo(
            [OrderStatus.Pending, OrderStatus.Shipped, OrderStatus.Cancelled],
            "unweighted statuses must never be drawn");

        // Same seed, second scenario: draw-for-draw identical (v1 D8 upheld — W4-D5).
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 42, Ct);
        var secondRun = Enumerable.Range(0, 500)
            .Select(i => (OrderEntity)runtime.Generate(order!, out _, []))
            .Select(o => (o.Status, o.Total))
            .ToList();
        await runtime.EndScenarioAsync(Ct);
        secondRun.Should().Equal(firstRun);
    }

    [Fact]
    public async Task GeneratorPlugin_DiscoveredFromPluginAssemblies_ExtendsTheAutoFaker()
    {
        // The test assembly plays the role of a plugin assembly carrying the generator.
        await using var runtime = DomainRuntimeBuilder.Build(_domains.Settings.Domains[0], _domains.Settings,
            [typeof(OrdersDbContext).Assembly, typeof(TestOrderNumberGenerator).Assembly]);
        runtime.TryResolveEntity("Order", out var order, out _).Should().BeTrue();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 7, Ct);
        var first = (OrderEntity)runtime.Generate(order!, out var fakerSource, []);
        var sequence = Enumerable.Range(0, 5)
            .Select(i => ((OrderEntity)runtime.Generate(order!, out _, [])).OrderNumber).ToList();
        await runtime.EndScenarioAsync(Ct);

        first.OrderNumber.Should().StartWith("ORD-PLUGIN-");
        fakerSource.Should().Be("auto+plugin:TestOrderNumbers");

        // Plugin draws ride the same per-scenario Randomizer — same seed, same numbers.
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 7, Ct);
        ((OrderEntity)runtime.Generate(order!, out _, [])).OrderNumber.Should().Be(first.OrderNumber);
        Enumerable.Range(0, 5).Select(i => ((OrderEntity)runtime.Generate(order!, out _, [])).OrderNumber)
            .Should().Equal(sequence);
        await runtime.EndScenarioAsync(Ct);

        // Products have a convention ProductFaker — plugins extend the *auto*-faker only.
        runtime.TryResolveEntity("Product", out var product, out _).Should().BeTrue();
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 7, Ct);
        runtime.Generate(product!, out var productSource, []);
        await runtime.EndScenarioAsync(Ct);
        productSource.Should().Be("ProductFaker");
    }

    [Fact]
    public async Task Locale_PicksVocabulary_InvalidLocaleFailsActionably()
    {
        _domains.Settings.Domains[0].Locale = "de";
        await using var runtime = _domains.BuildOrders();
        runtime.TryResolveEntity("Warehouse", out var warehouse, out _).Should().BeTrue();

        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 3, Ct);
        var first = (WarehouseEntity)runtime.Generate(warehouse!, out _, []);
        await runtime.EndScenarioAsync(Ct);
        await runtime.BeginScenarioAsync(LifecycleMode.Persistent, seed: 3, Ct);
        var second = (WarehouseEntity)runtime.Generate(warehouse!, out _, []);
        await runtime.EndScenarioAsync(Ct);
        second.Name.Should().Be(first.Name, "locale changes vocabulary, not determinism");

        _domains.Settings.Domains[0].Locale = "xx_NOPE";
        await using var invalid = _domains.BuildOrders();
        await FluentActions.Awaiting(() => invalid.BeginScenarioAsync(LifecycleMode.Persistent, seed: 1, Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a valid Bogus locale*en_GB*");
    }
}
