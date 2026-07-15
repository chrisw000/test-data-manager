using Microsoft.EntityFrameworkCore;
using Tdm.Registry;
using Tdm.Registry.Client;

var builder = WebApplication.CreateBuilder(args);

// One small DB (W2-D7). TDM_REGISTRY_DB overrides the SQLite path; tests inject their own options.
var dbPath = Environment.GetEnvironmentVariable("TDM_REGISTRY_DB") ?? "./tdm-registry.db";
builder.Services.AddDbContext<RegistryDb>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<RegistryDb>().Database.EnsureCreated();
}

// Optional shared-key auth: setting TDM_REGISTRY_APIKEY requires every request to carry it
// as X-Tdm-ApiKey. Unset = open (local/dev). Real credential management is W2-P4's
// secrets-provider chain on the client side; the service never stores anything else.
var requiredApiKey = Environment.GetEnvironmentVariable("TDM_REGISTRY_APIKEY");
if (!string.IsNullOrEmpty(requiredApiKey))
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Headers.TryGetValue("X-Tdm-ApiKey", out var supplied) ||
            !string.Equals(supplied.ToString(), requiredApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });
}

// ---------------------------------------------------------------- Runs (index)

app.MapPost("/runs", async (StartRunRequest request, RegistryDb db, TimeProvider clock) =>
{
    var run = new RunRow
    {
        Id = Guid.NewGuid(),
        Environment = request.Environment,
        Name = request.Name,
        SettingsSha256 = request.SettingsSha256,
        RunnerId = request.RunnerId,
        StartedUtc = clock.GetUtcNow().UtcDateTime,
    };
    db.Runs.Add(run);
    await db.SaveChangesAsync();
    return Results.Created($"/runs/{run.Id}", new StartRunResponse(run.Id));
});

app.MapPatch("/runs/{id:guid}", async (Guid id, FinishRunRequest request, RegistryDb db, TimeProvider clock) =>
{
    var run = await db.Runs.FindAsync(id);
    if (run is null) return Results.NotFound();
    run.FinishedUtc = clock.GetUtcNow().UtcDateTime;
    run.Outcome = request.Outcome;
    run.ManifestUrl = request.ManifestUrl;
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/runs", async (string? environment, RegistryDb db) =>
{
    var query = db.Runs.AsNoTracking();
    if (!string.IsNullOrEmpty(environment)) query = query.Where(r => r.Environment == environment);
    var runs = await query.OrderByDescending(r => r.StartedUtc).Take(100).ToListAsync();
    return Results.Ok(runs.Select(r => new RunRecord(
        r.Id, r.Environment, r.Name, r.SettingsSha256, r.RunnerId, r.StartedUtc, r.FinishedUtc, r.Outcome, r.ManifestUrl)));
});

// ---------------------------------------------------------------- Locks (leases)

app.MapPost("/locks", async (AcquireLockRequest request, RegistryDb db, TimeProvider clock) =>
{
    var now = clock.GetUtcNow().UtcDateTime;
    var ttl = TimeSpan.FromSeconds(Math.Clamp(request.TtlSeconds, 5, 3600));
    // Null environment normalises to "" — SQLite unique indexes treat NULLs as distinct,
    // which would let two no-environment runs lease the same database simultaneously.
    var environment = request.Environment ?? "";

    // Reap expired leases for this key, then insert — the unique index on
    // (Environment, Domain, DatabaseId) turns a concurrent race into a 409.
    var expired = db.Locks.Where(l =>
        l.Environment == environment && l.Domain == request.Domain &&
        l.DatabaseId == request.DatabaseId && l.ExpiresUtc <= now);
    db.Locks.RemoveRange(expired);
    await db.SaveChangesAsync();

    var lease = new LockRow
    {
        Id = Guid.NewGuid(),
        Environment = environment,
        Domain = request.Domain,
        DatabaseId = request.DatabaseId,
        RunId = request.RunId,
        Holder = request.Holder,
        AcquiredUtc = now,
        ExpiresUtc = now + ttl,
        TtlSeconds = (int)ttl.TotalSeconds,
    };
    db.Locks.Add(lease);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        db.Entry(lease).State = EntityState.Detached;
        var holder = await db.Locks.AsNoTracking().FirstOrDefaultAsync(l =>
            l.Environment == environment && l.Domain == request.Domain && l.DatabaseId == request.DatabaseId);
        return holder is null
            ? Results.Conflict(new LockConflictResponse(environment, request.Domain, request.DatabaseId,
                "unknown (lease changed hands mid-request — retry)", null, now, now))
            : Results.Conflict(new LockConflictResponse(holder.Environment, holder.Domain, holder.DatabaseId,
                holder.Holder, holder.RunId, holder.AcquiredUtc, holder.ExpiresUtc));
    }
    return Results.Created($"/locks/{lease.Id}", new LockGrantedResponse(lease.Id, lease.ExpiresUtc));
});

app.MapPost("/locks/{id:guid}/heartbeat", async (Guid id, RegistryDb db, TimeProvider clock) =>
{
    var now = clock.GetUtcNow().UtcDateTime;
    var lease = await db.Locks.FindAsync(id);
    if (lease is null || lease.ExpiresUtc <= now) return Results.NotFound();
    lease.ExpiresUtc = now + TimeSpan.FromSeconds(lease.TtlSeconds);
    await db.SaveChangesAsync();
    return Results.Ok(new HeartbeatResponse(lease.ExpiresUtc));
});

app.MapDelete("/locks/{id:guid}", async (Guid id, RegistryDb db) =>
{
    var lease = await db.Locks.FindAsync(id);
    if (lease is null) return Results.NotFound();
    db.Locks.Remove(lease);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapGet("/locks", async (string? environment, RegistryDb db, TimeProvider clock) =>
{
    var now = clock.GetUtcNow().UtcDateTime;
    var query = db.Locks.AsNoTracking().Where(l => l.ExpiresUtc > now);
    if (!string.IsNullOrEmpty(environment)) query = query.Where(l => l.Environment == environment);
    var locks = await query.OrderBy(l => l.AcquiredUtc).ToListAsync();
    return Results.Ok(locks.Select(l => new LockRecord(
        l.Id, l.Environment, l.Domain, l.DatabaseId, l.Holder, l.RunId, l.AcquiredUtc, l.ExpiresUtc)));
});

app.Run();

/// <summary>Exposed for WebApplicationFactory-based tests.</summary>
public partial class Program;

namespace Tdm.Registry
{
    public sealed class RegistryDb(DbContextOptions<RegistryDb> options) : DbContext(options)
    {
        public DbSet<RunRow> Runs => Set<RunRow>();
        public DbSet<LockRow> Locks => Set<LockRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // One live lease per (environment, domain, database) — the concurrency guarantee.
            modelBuilder.Entity<LockRow>()
                .HasIndex(l => new { l.Environment, l.Domain, l.DatabaseId })
                .IsUnique();
        }
    }

    public sealed class RunRow
    {
        public Guid Id { get; set; }
        public string? Environment { get; set; }
        public string Name { get; set; } = "";
        public string? SettingsSha256 { get; set; }
        public string? RunnerId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string? Outcome { get; set; }
        public string? ManifestUrl { get; set; }
    }

    public sealed class LockRow
    {
        public Guid Id { get; set; }
        public string? Environment { get; set; }
        public string Domain { get; set; } = "";
        public string DatabaseId { get; set; } = "";
        public Guid? RunId { get; set; }
        public string? Holder { get; set; }
        public DateTime AcquiredUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public int TtlSeconds { get; set; }
    }
}
