using System.Text;
using Microsoft.EntityFrameworkCore;
using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Identity;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>
/// The W3-P1 acceptance criterion against real EF/SQLite: a parallel run yields an identical
/// manifest (values and identities) to a serial run, modulo timings — and runtime sessions
/// (W3-D2) keep per-scenario state isolated while sharing the build-once bindings.
/// </summary>
public class ParallelExecutionEfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeedingPlan Plan(string text) =>
        new() { Features = { new GherkinPlanParser().ParseText(text) } };

    private static string BuildFeature(int scenarios)
    {
        var sb = new StringBuilder("Feature: ParallelSeed\n");
        for (var i = 1; i <= scenarios; i++)
        {
            sb.Append(
                $"  Scenario: Seed {i}\n" +
                $"    Given a Customer exists with name \"Par-{i}\" and tier \"Tier-{i}\"\n" +
                $"    And an Order exists for Customer \"Par-{i}\" with order number \"PAR-ORD-{i}\" and status \"Pending\"\n");
        }
        return sb.ToString();
    }

    [Fact]
    public async Task ParallelRun_YieldsIdenticalManifestAndRows_ToSerialRun()
    {
        var feature = BuildFeature(scenarios: 6);

        await using var serialDomains = new TestDomains();
        serialDomains.Settings.Run.Lifecycle = LifecycleMode.Persistent;
        await using var serialOrders = serialDomains.BuildOrders();
        var serial = await new TdmEngine(serialDomains.Settings, [serialOrders]).RunAsync(Plan(feature), ct: Ct);

        await using var parallelDomains = new TestDomains();
        parallelDomains.Settings.Run.Lifecycle = LifecycleMode.Persistent;
        parallelDomains.Settings.Run.MaxParallelScenarios = 6;
        await using var parallelOrders = parallelDomains.BuildOrders();
        var parallel = await new TdmEngine(parallelDomains.Settings, [parallelOrders]).RunAsync(Plan(feature), ct: Ct);

        serial.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        parallel.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        parallel.Scenarios.Select(s => s.Scenario).Should().Equal(serial.Scenarios.Select(s => s.Scenario));

        foreach (var (parallelScenario, serialScenario) in parallel.Scenarios.Zip(serial.Scenarios))
        {
            parallelScenario.Outcome.Should().Be(serialScenario.Outcome);
            parallelScenario.Entities.Select(e => (e.Ordinal, e.Entity, e.Verb, e.Id, e.NaturalKey, e.PersistedVia))
                .Should().Equal(serialScenario.Entities.Select(e => (e.Ordinal, e.Entity, e.Verb, e.Id, e.NaturalKey, e.PersistedVia)));

            foreach (var (parallelEntity, serialEntity) in parallelScenario.Entities.Zip(serialScenario.Entities))
                ShouldMatchModuloTimings(parallelEntity.Values, serialEntity.Values);
        }

        await using var serialDb = serialDomains.NewOrdersContext();
        await using var parallelDb = parallelDomains.NewOrdersContext();
        (await parallelDb.Customers.CountAsync(Ct)).Should().Be(await serialDb.Customers.CountAsync(Ct));
        (await parallelDb.Orders.CountAsync(Ct)).Should().Be(await serialDb.Orders.CountAsync(Ct));
        (await parallelDb.Orders.CountAsync(Ct)).Should().Be(6);
    }

    /// <summary>
    /// "Identical values and identities, modulo timings": everything must match to the byte,
    /// except values anchored to the wall clock at generation time (audit stamps, Bogus
    /// time-anchored dates like f.Date.Recent) — those may differ by the moments between the
    /// two runs, never by more.
    /// </summary>
    private static void ShouldMatchModuloTimings(Dictionary<string, string?> actual, Dictionary<string, string?> expected)
    {
        actual.Keys.Should().BeEquivalentTo(expected.Keys);
        foreach (var (key, expectedValue) in expected)
        {
            var actualValue = actual[key];
            if (string.Equals(actualValue, expectedValue, StringComparison.Ordinal)) continue;

            if (DateTime.TryParse(expectedValue, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var expectedTime) &&
                DateTime.TryParse(actualValue, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var actualTime))
            {
                (actualTime - expectedTime).Duration().Should().BeLessThan(TimeSpan.FromMinutes(5),
                    $"'{key}' is time-anchored and may differ only by run timing");
                continue;
            }

            actualValue.Should().Be(expectedValue, $"'{key}' must be deterministic across serial and parallel runs");
        }
    }

    [Fact]
    public async Task Sessions_ShareBindings_ButTrackScenarioStateIndependently()
    {
        await using var domains = new TestDomains();
        await using var root = domains.BuildOrders();
        await using var first = root.CreateSession();
        await using var second = root.CreateSession();

        first.Should().NotBeSameAs(root);
        first.Should().NotBeSameAs(second);
        first.Entities.Should().BeSameAs(root.Entities); // build-once bindings, shared
        first.PolicyViolations.Should().BeSameAs(root.PolicyViolations);

        await first.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 1, Ct);
        await second.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 2, Ct);

        root.TryResolveEntity("Customer", out var customer, out _).Should().BeTrue();
        var rowA = first.Generate(customer!, out _, []);
        customer!.NaturalKeyProperty!.SetValue(rowA, "Iso-A");
        customer.SetKey(rowA, TdmIdentity.ForNaturalKey("Orders", "Customer", "Iso-A"));
        var rowB = second.Generate(customer, out _, []);
        customer.NaturalKeyProperty!.SetValue(rowB, "Iso-B");
        customer.SetKey(rowB, TdmIdentity.ForNaturalKey("Orders", "Customer", "Iso-B"));

        (await first.CreateAsync(customer, rowA, ct: Ct)).Success.Should().BeTrue();
        (await second.CreateAsync(customer, rowB, ct: Ct)).Success.Should().BeTrue();

        // Ending the first session tears down only its own tracked rows.
        var closeFirst = await first.EndScenarioAsync(Ct);
        closeFirst.Deleted.Should().Be(1);
        await using (var db = domains.NewOrdersContext())
        {
            var remaining = await db.Customers.ToListAsync(Ct);
            remaining.Should().ContainSingle().Which.Name.Should().Be("Iso-B");
        }

        var closeSecond = await second.EndScenarioAsync(Ct);
        closeSecond.Deleted.Should().Be(1);
        await using (var db = domains.NewOrdersContext())
        {
            (await db.Customers.CountAsync(Ct)).Should().Be(0);
        }
    }
}
