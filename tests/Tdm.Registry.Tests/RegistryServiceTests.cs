using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Tdm.Registry;
using Tdm.Registry.Client;
using Xunit;

namespace Tdm.Registry.Tests;

/// <summary>
/// In-proc service + real RegistryClient round-trips (W2-P3): every test exercises both the
/// wire contract and the client in one go. A FakeTimeProvider drives TTL expiry without
/// sleeping; each factory gets its own temp SQLite file.
/// </summary>
public sealed class RegistryFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"tdm-registry-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-07-15T12:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<RegistryDb>>();
            services.AddDbContext<RegistryDb>(options => options.UseSqlite($"Data Source={_dbPath}"));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public RegistryClient NewClient() => new(CreateClient());

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}

public class RegistryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AcquireLockRequest Lock(string? environment = "shared-dev", string domain = "Orders",
        string databaseId = "db-1", Guid? runId = null, string? holder = "orders-seed (local:chris)", int ttl = 60) =>
        new(environment, domain, databaseId, runId, holder, ttl);

    // ---------------------------------------------------------------- Runs

    [Fact]
    public async Task StartRun_FinishRun_RoundTrip_VisibleInIndex()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var runId = await client.StartRunAsync(
            new StartRunRequest("shared-dev", "orders-seed", "abc123", "local:chris"), Ct);
        await client.FinishRunAsync(runId, "Succeeded", "https://artifacts/run.tdm.json", Ct);

        var runs = await factory.CreateClient().GetFromJsonAsync<List<RunRecord>>("/runs?environment=shared-dev", Ct);
        var run = runs.Should().ContainSingle().Subject;
        run.Id.Should().Be(runId);
        run.Name.Should().Be("orders-seed");
        run.SettingsSha256.Should().Be("abc123");
        run.RunnerId.Should().Be("local:chris");
        run.Outcome.Should().Be("Succeeded");
        run.ManifestUrl.Should().Be("https://artifacts/run.tdm.json");
        run.FinishedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task FinishRun_UnknownId_404()
    {
        using var factory = new RegistryFactory();
        await FluentActions.Awaiting(() => factory.NewClient().FinishRunAsync(Guid.NewGuid(), "Succeeded", null, Ct))
            .Should().ThrowAsync<HttpRequestException>();
    }

    // ---------------------------------------------------------------- Locks

    [Fact]
    public async Task AcquireLock_Granted_ThenSecondConflicts_NamingHolder()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var first = await client.AcquireLockAsync(Lock(), Ct);
        first.IsGranted.Should().BeTrue();

        var second = await client.AcquireLockAsync(Lock(holder: "billing-seed (ci:jenkins)"), Ct);
        second.IsGranted.Should().BeFalse();
        second.Conflict!.Holder.Should().Be("orders-seed (local:chris)");
        second.Conflict.Domain.Should().Be("Orders");
    }

    [Fact]
    public async Task DifferentDatabases_AndDifferentEnvironments_DoNotConflict()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        (await client.AcquireLockAsync(Lock(databaseId: "db-1"), Ct)).IsGranted.Should().BeTrue();
        (await client.AcquireLockAsync(Lock(databaseId: "db-2"), Ct)).IsGranted.Should().BeTrue();
        (await client.AcquireLockAsync(Lock(environment: "staging", databaseId: "db-1"), Ct)).IsGranted.Should().BeTrue();
    }

    [Fact]
    public async Task NullEnvironment_StillConflicts()
    {
        // SQLite unique indexes treat NULLs as distinct — the service normalises null
        // environment to "" so no-environment runs still collide on the same database.
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        (await client.AcquireLockAsync(Lock(environment: null), Ct)).IsGranted.Should().BeTrue();
        (await client.AcquireLockAsync(Lock(environment: null), Ct)).IsGranted.Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseLock_AllowsReacquire()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var first = await client.AcquireLockAsync(Lock(), Ct);
        await client.ReleaseLockAsync(first.Granted!.Id, Ct);
        (await client.AcquireLockAsync(Lock(), Ct)).IsGranted.Should().BeTrue();
    }

    [Fact]
    public async Task ExpiredLease_IsReaped_OnNextAcquire()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        (await client.AcquireLockAsync(Lock(ttl: 60), Ct)).IsGranted.Should().BeTrue();
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        (await client.AcquireLockAsync(Lock(holder: "second-run"), Ct)).IsGranted.Should().BeTrue();
    }

    [Fact]
    public async Task Heartbeat_ExtendsLease_PastOriginalTtl()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var granted = (await client.AcquireLockAsync(Lock(ttl: 60), Ct)).Granted!;

        factory.Clock.Advance(TimeSpan.FromSeconds(50));
        var newExpiry = await client.HeartbeatAsync(granted.Id, Ct);
        newExpiry.Should().BeAfter(granted.ExpiresUtc);

        // 70s after acquisition — past the original TTL, inside the renewed lease.
        factory.Clock.Advance(TimeSpan.FromSeconds(20));
        (await client.AcquireLockAsync(Lock(holder: "second-run"), Ct)).IsGranted.Should().BeFalse();
    }

    [Fact]
    public async Task Heartbeat_OnExpiredLease_Fails()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var granted = (await client.AcquireLockAsync(Lock(ttl: 60), Ct)).Granted!;
        factory.Clock.Advance(TimeSpan.FromSeconds(61));

        await FluentActions.Awaiting(() => client.HeartbeatAsync(granted.Id, Ct))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ReleaseLock_AlreadyReaped_IsBestEffortNoop()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        var granted = (await client.AcquireLockAsync(Lock(ttl: 60), Ct)).Granted!;
        factory.Clock.Advance(TimeSpan.FromSeconds(61));
        await client.AcquireLockAsync(Lock(holder: "reaper"), Ct); // reaps the expired lease

        await client.ReleaseLockAsync(granted.Id, Ct); // 404 swallowed by design
    }

    [Fact]
    public async Task GetLocks_ListsOnlyActiveLeases()
    {
        using var factory = new RegistryFactory();
        var client = factory.NewClient();

        await client.AcquireLockAsync(Lock(databaseId: "db-1", ttl: 60), Ct);
        await client.AcquireLockAsync(Lock(databaseId: "db-2", ttl: 300), Ct);
        factory.Clock.Advance(TimeSpan.FromSeconds(61)); // first expires

        var locks = await factory.CreateClient().GetFromJsonAsync<List<LockRecord>>("/locks", Ct);
        locks.Should().ContainSingle().Which.DatabaseId.Should().Be("db-2");
    }
}
