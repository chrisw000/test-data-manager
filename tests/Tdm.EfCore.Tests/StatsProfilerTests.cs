using Acme.Billing.Data.Infrastructure;
using Acme.Orders.Data.Persistence;
using Acme.Orders.Data.Persistence.Domain;
using AwesomeAssertions;
using Bogus;
using Tdm.Core.Generation;
using Tdm.Core.Profiling;
using Tdm.Core.Settings;
using Tdm.EfCore.Profiling;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>
/// W4-D8 spike tests: `tdm profile` computes shapes, never row values — the only captured
/// literals are low-cardinality category labels, and even those can be suppressed.
/// </summary>
public class StatsProfilerTests : IAsyncDisposable
{
    private readonly TestDomains _domains = new();

    public async ValueTask DisposeAsync() => await _domains.DisposeAsync();

    /// <summary>100 customers: Tier 60/30/10, unique names, right-skewed credit limits.</summary>
    private void SeedCustomers()
    {
        using var context = _domains.NewOrdersContext();
        context.Database.EnsureCreated();
        for (var i = 0; i < 100; i++)
        {
            context.Customers.Add(new CustomerEntity
            {
                Id = Guid.NewGuid(),
                Name = $"cust-{i:000}",
                Tier = i < 60 ? "Gold" : i < 90 ? "Silver" : "Bronze",
                Email = $"c{i}@example.test",
                CreditLimit = i < 90 ? 10m : 1000m, // long right tail, all positive
                CreatedUtc = DateTime.UtcNow,
            });
        }
        context.SaveChanges();
    }

    private StatsPack ProfileOrders(ProfileOptions? options = null) =>
        StatsProfiler.Profile(_domains.Settings.Domains[0], _domains.Settings,
            [typeof(OrdersDbContext).Assembly], options ?? new ProfileOptions());

    [Fact]
    public void CategoricalWeights_Captured_HighCardinalityAndKeys_Never()
    {
        SeedCustomers();
        var pack = ProfileOrders();

        var customer = pack.Entities["Customer"];
        customer.Rows.Should().Be(100);

        // Low-cardinality category labels: the one permitted literal capture.
        var tier = customer.Properties["Tier"];
        tier.Distinct.Should().Be(3);
        tier.Weights.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["Gold"] = 0.6, ["Silver"] = 0.3, ["Bronze"] = 0.1,
        });
        tier.Suggested!.Weights.Should().NotBeNull();

        // High-cardinality strings: cardinality only, no values anywhere in the pack.
        var name = customer.Properties["Name"];
        name.Distinct.Should().Be(100);
        name.Weights.Should().BeNull();
        pack.Serialize().Should().NotContain("cust-0", "row values must never appear in a stats pack");

        // Keys and FKs are identity, not shape — not profiled at all.
        customer.Properties.Should().NotContainKey("Id");
    }

    [Fact]
    public void NumericColumns_GetMoments_AndASkewAwareDistributionFit()
    {
        SeedCustomers();
        var pack = ProfileOrders();

        var creditLimit = pack.Entities["Customer"].Properties["CreditLimit"];
        creditLimit.Min.Should().Be(10);
        creditLimit.Max.Should().Be(1000);
        creditLimit.Mean.Should().Be(109); // 90×10 + 10×1000 over 100
        creditLimit.Median.Should().Be(10);

        // Non-negative + right-skewed ⇒ lognormal, mean = the sample median (§2.3 convention).
        creditLimit.Suggested!.Distribution.Should().Be("lognormal");
        creditLimit.Suggested.Mean.Should().Be(10);
        creditLimit.Suggested.Sigma.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CorrelationHints_FlagNearFunctionalDependencies()
    {
        using (var context = _domains.NewBillingContext())
        {
            context.Database.EnsureCreated();
            for (var i = 0; i < 100; i++)
            {
                context.Accounts.Add(new AccountModel
                {
                    Id = Guid.NewGuid(),
                    Name = $"acc-{i:000}",
                    Currency = i < 50 ? "GBP" : "EUR",
                    Active = i < 50, // Currency fully determines Active — a correlated pair
                    Balance = i,
                });
            }
            context.SaveChanges();
        }

        var pack = StatsProfiler.Profile(_domains.Settings.Domains[1], _domains.Settings,
            [typeof(BillingDbContext).Assembly], new ProfileOptions());

        var hint = pack.Entities["Account"].CorrelationHints.Should().ContainSingle().Subject;
        hint.Should().BeEquivalentTo(["Currency", "Active"]);
    }

    [Fact]
    public void NoValues_SuppressesCategoryLabelsEntirely()
    {
        SeedCustomers();
        var pack = ProfileOrders(new ProfileOptions(IncludeValues: false));

        pack.ValuesSuppressed.Should().BeTrue();
        var tier = pack.Entities["Customer"].Properties["Tier"];
        tier.Distinct.Should().Be(3, "cardinality survives");
        tier.Weights.Should().BeNull();
        tier.Suggested.Should().BeNull();
        pack.Serialize().Should().NotContain("Gold").And.NotContain("cust-0");
    }

    /// <summary>The full W4-D8 circle: profile a source → fragment → §2.3 generation
    /// reproduces the source's shape, without a single row having been copied.</summary>
    [Fact]
    public void RoundTrip_ProfiledShape_DrivesStatisticalGeneration()
    {
        SeedCustomers();
        var fragment = ProfileOrders().ToFragment();

        // Consume the fragment exactly as a seed pack would (W4-D7 merge, local wins).
        var settings = new TdmSettings();
        settings.Entities["Customer"] = fragment.Entities["Customer"];
        var generator = new StatisticalGenerator(settings);
        var descriptor = new Tdm.Core.Execution.EntityDescriptor
        {
            LogicalName = "Customer",
            DomainName = "Orders",
            ClrType = typeof(CustomerEntity),
            KeyProperty = typeof(CustomerEntity).GetProperty(nameof(CustomerEntity.Id)),
            NaturalKeyProperty = typeof(CustomerEntity).GetProperty(nameof(CustomerEntity.Name)),
            NavigationNames = [nameof(CustomerEntity.Orders)],
        };

        var random = new Randomizer(42);
        var tiers = new Dictionary<string, int>();
        var limits = new List<decimal>();
        for (var i = 0; i < 5_000; i++)
        {
            var instance = new CustomerEntity();
            generator.Apply(descriptor, instance, random);
            tiers[instance.Tier] = tiers.GetValueOrDefault(instance.Tier) + 1;
            limits.Add(instance.CreditLimit);
        }

        (tiers["Gold"] / 5_000d).Should().BeApproximately(0.6, 0.03);
        (tiers["Silver"] / 5_000d).Should().BeApproximately(0.3, 0.03);
        (tiers["Bronze"] / 5_000d).Should().BeApproximately(0.1, 0.03);
        limits.Should().OnlyContain(l => l >= 0);
        limits.OrderBy(l => l).ElementAt(2_500).Should().BeInRange(5m, 20m, "the lognormal scale is the source median");
    }
}
