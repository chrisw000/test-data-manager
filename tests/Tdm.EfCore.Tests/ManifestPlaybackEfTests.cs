using Acme.Orders.Data.Persistence.Domain;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.EfCore.Tests;

/// <summary>
/// The W2 acceptance criterion, end to end against real EF/SQLite: replay of a manifest
/// reproduces identical rows in a fresh database, and verify detects drift afterwards.
/// </summary>
public class ManifestPlaybackEfTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeedingPlan Plan(string text) =>
        new() { Features = { new GherkinPlanParser().ParseText(text) } };

    [Fact]
    public async Task Replay_IntoFreshDatabase_ReproducesRows_ThenVerifyDetectsDrift()
    {
        // 1. Seed an environment (creates, a reference, an update) and capture its manifest.
        await using var original = new TestDomains();
        original.Settings.Run.Lifecycle = LifecycleMode.Persistent;
        await using var seededOrders = original.BuildOrders();
        var manifest = await new TdmEngine(original.Settings, [seededOrders]).RunAsync(Plan("""
            Feature: F
              Scenario: seed
                Given a Customer exists with name "Acme Ltd" and tier "Gold"
                And an Order exists for Customer "Acme Ltd" with order number "ORD-1" and status "Pending"
                And the Customer "Acme Ltd" is updated with tier "Platinum"
            """), ct: Ct);
        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);

        // 2. Replay into a completely fresh database — no feature files, manifest only.
        await using var fresh = new TestDomains();
        fresh.Settings.Run.Lifecycle = LifecycleMode.Persistent;
        await using var freshOrders = fresh.BuildOrders();
        var replay = await ManifestPlayback.ReplayAsync(manifest, [freshOrders], ct: Ct);
        replay.Failures.Should().BeEmpty();
        replay.Created.Should().Be(2); // Customer + Order

        // 3. Identical rows: verifying the fresh database against the original manifest
        //    finds no drift — including audit stamps, decimals, FK ids and the post-update
        //    tier. A separate runtime so the check reads the database, not the replaying
        //    runtime's change tracker (which would mask round-trip asymmetries).
        await using var cleanVerifier = fresh.BuildOrders();
        var verify = await ManifestPlayback.VerifyAsync(manifest, [cleanVerifier], ct: Ct);
        verify.Drift.Should().BeEmpty();
        verify.ExitCode.Should().Be(0);
        verify.RowsChecked.Should().BeGreaterThanOrEqualTo(2);

        await using (var db = fresh.NewOrdersContext())
        {
            (await db.Customers.SingleAsync(c => c.Name == "Acme Ltd", Ct)).Tier.Should().Be("Platinum");
            var order = await db.Orders.SingleAsync(o => o.OrderNumber == "ORD-1", Ct);
            order.CustomerId.Should().NotBe(Guid.Empty); // DB-resolved reference id reproduced
        }

        // 4. Vandalise a value — verify reports the drift naming property and both values.
        await using (var db = fresh.NewOrdersContext())
        {
            var customer = await db.Customers.SingleAsync(c => c.Name == "Acme Ltd", Ct);
            customer.Tier = "Bronze";
            await db.SaveChangesAsync(Ct);
        }
        // A fresh runtime, as every real `tdm verify` invocation gets — the previous runtime's
        // change tracker still holds the pre-vandalism instance.
        await using var verifierOrders = fresh.BuildOrders();
        var drifted = await ManifestPlayback.VerifyAsync(manifest, [verifierOrders], ct: Ct);
        drifted.ExitCode.Should().Be(1);
        drifted.Drift.Should().ContainSingle().Which
            .Should().Contain("Tier").And.Contain("Bronze").And.Contain("Platinum");
    }
}
