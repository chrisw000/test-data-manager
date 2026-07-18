using System.Text.Json;
using Acme.Orders.Data.Persistence;
using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Identity;
using Xunit;

namespace Tdm.Api.Tests;

/// <summary>
/// The wave-4 acceptance criterion (§5): the Orders sample domain seeds successfully through
/// a stub HTTP API with TrackedTeardown deleting via API in reverse order — and the engine
/// code is unchanged: these tests run the real <see cref="TdmEngine"/> against
/// <see cref="ApiDomainRuntime"/> as just another <see cref="IDomainRuntime"/> (W4-D6).
/// </summary>
public sealed class ApiSeedingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly StubApiServer _server = new();

    public void Dispose() => _server.Dispose();

    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private TdmSettings OrdersApiSettings(LifecycleMode lifecycle = LifecycleMode.TrackedTeardown)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings { Name = "api-test", Lifecycle = lifecycle },
            Domains =
            [
                new DomainSettings
                {
                    Name = "Orders",
                    Persistence = PersistenceMode.Api,
                    ConventionProfile = "modern",
                    Api = new ApiSettings
                    {
                        BaseUrl = _server.BaseUrl,
                        Entities =
                        {
                            ["Customer"] = new ApiEntityEndpoints
                            {
                                Create = "POST /api/customers",
                                Update = "PUT /api/customers/{id}",
                                Delete = "DELETE /api/customers/{id}",
                                GetByKey = "GET /api/customers?name={key}",
                            },
                            ["Order"] = new ApiEntityEndpoints
                            {
                                Create = "POST /api/orders",
                                Delete = "DELETE /api/orders/{id}",
                                GetByKey = "GET /api/orders?number={key}",
                            },
                        },
                    },
                },
            ],
            Entities =
            {
                ["Order"] = new EntitySettings { NaturalKey = "OrderNumber" },
            },
        };
        settings.ApplyDefaults();
        return settings;
    }

    private ApiDomainRuntime BuildRuntime(TdmSettings settings, string? authToken = null) =>
        ApiRuntimeBuilder.Build(settings.Domains[0], settings,
            [typeof(OrdersDbContext).Assembly], logger: null, authToken);

    [Fact]
    public async Task OrdersDomain_SeedsThroughTheApi_TrackedTeardownDeletesInReverseOrder()
    {
        _server.OnRequest = request => request.Method switch
        {
            "POST" => (201, request.Body), // echo — client-set ids come straight back
            "DELETE" => (204, null),
            _ => (404, null),
        };

        var settings = OrdersApiSettings();
        await using var runtime = BuildRuntime(settings);
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: Api seeding
              Scenario: Customer places an order
                Given a Customer exists with name "Acme Ltd" and tier "Gold"
                And an Order exists for Customer "Acme Ltd" with status "Pending"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var scenario = manifest.Scenarios.Should().ContainSingle().Subject;
        scenario.Teardown!.Deleted.Should().Be(2);
        scenario.Teardown.Orphaned.Should().BeEmpty();

        var customerId = TdmIdentity.ForNaturalKey("Orders", "Customer", "Acme Ltd").ToString();
        var writes = _server.Requests.Where(r => r.Method is "POST" or "DELETE").ToList();
        writes.Select(r => (r.Method, r.PathAndQuery.Split('?')[0])).Should().Equal(
        [
            ("POST", "/api/customers"),
            ("POST", "/api/orders"),
            ("DELETE", $"/api/orders/{scenario.Entities[1].Id}"),   // reverse creation order
            ("DELETE", $"/api/customers/{customerId}"),
        ]);

        // Client-set identity-contract ids ride the payloads (W4-D6).
        using var customerPayload = JsonDocument.Parse(writes[0].Body);
        customerPayload.RootElement.GetProperty("id").GetString().Should().Be(customerId);
        customerPayload.RootElement.GetProperty("name").GetString().Should().Be("Acme Ltd");
        customerPayload.RootElement.GetProperty("tier").GetString().Should().Be("Gold");

        // The order carries the FK to the referenced customer and no navigation payload.
        using var orderPayload = JsonDocument.Parse(writes[1].Body);
        orderPayload.RootElement.GetProperty("customerId").GetString().Should().Be(customerId);
        orderPayload.RootElement.GetProperty("status").GetString().Should().Be("Pending");
        orderPayload.RootElement.TryGetProperty("customer", out _).Should().BeFalse();

        scenario.Entities[0].PersistedVia.Should().Be("POST /api/customers");
    }

    [Fact]
    public async Task ServerAssignedIds_AreCapturedFromResponses_LikeDbGeneratedInts()
    {
        _server.OnRequest = request => request.Method switch
        {
            "POST" => (201, """{ "id": 123, "invoiceNumber": "INV-1" }"""),
            "DELETE" => (204, null),
            _ => (404, null),
        };

        var settings = new TdmSettings
        {
            Run = new RunSettings { Name = "api-test", Lifecycle = LifecycleMode.TrackedTeardown },
            Domains =
            [
                new DomainSettings
                {
                    Name = "Billing", Persistence = PersistenceMode.Api, ConventionProfile = "legacy",
                    Api = new ApiSettings
                    {
                        BaseUrl = _server.BaseUrl,
                        Entities =
                        {
                            ["Invoice"] = new ApiEntityEndpoints
                            {
                                Create = "POST /api/invoices",
                                Delete = "DELETE /api/invoices/{id}",
                                GetByKey = "GET /api/invoices?number={key}",
                            },
                        },
                    },
                },
            ],
            Entities = { ["Invoice"] = new EntitySettings { NaturalKey = "InvoiceNumber" } },
        };
        settings.ApplyDefaults();

        await using var runtime = ApiRuntimeBuilder.Build(settings.Domains[0], settings,
            [typeof(Acme.Billing.Data.Infrastructure.BillingDbContext).Assembly]);
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given an Invoice exists with invoice number "INV-1" and amount "10.00"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var entry = manifest.Scenarios[0].Entities.Should().ContainSingle().Subject;
        entry.Id.Should().Be("123", "the server-assigned id is captured from the response");

        // Payload omitted the server-assigned key; teardown deleted by the captured id.
        using var payload = JsonDocument.Parse(_server.Requests.First(r => r.Method == "POST").Body);
        payload.RootElement.TryGetProperty("id", out _).Should().BeFalse();
        _server.Requests.Should().Contain(r => r.Method == "DELETE" && r.PathAndQuery == "/api/invoices/123");
    }

    [Fact]
    public async Task GetByKey_DrivesVerifySteps_AndAuthHeaderIsSent()
    {
        _server.OnRequest = request => request switch
        {
            { Method: "GET" } => (200, """{ "id": "00000000-0000-0000-0000-000000000001", "name": "Acme Ltd", "tier": "Gold" }"""),
            _ => (404, null),
        };

        var settings = OrdersApiSettings(LifecycleMode.Persistent);
        settings.Domains[0].Api!.Auth = new ApiAuthSettings { TokenSecret = "ORDERS_API_TOKEN" };
        await using var runtime = BuildRuntime(settings, authToken: "s3cr3t");
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Then a Customer "Acme Ltd" should exist with tier "Gold"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var get = _server.Requests.Should().ContainSingle().Subject;
        get.PathAndQuery.Should().Be("/api/customers?name=Acme%20Ltd");
        get.Authorization.Should().Be("Bearer s3cr3t");
    }

    [Fact]
    public async Task Update_FindsByKey_ThenPutsById()
    {
        var customerId = TdmIdentity.ForNaturalKey("Orders", "Customer", "Acme Ltd").ToString();
        _server.OnRequest = request => request switch
        {
            { Method: "GET" } => (200, $$"""{ "id": "{{customerId}}", "name": "Acme Ltd", "tier": "Gold" }"""),
            { Method: "PUT" } => (200, null),
            _ => (404, null),
        };

        var settings = OrdersApiSettings(LifecycleMode.Persistent);
        await using var runtime = BuildRuntime(settings);
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                When the Customer "Acme Ltd" is updated with tier "Platinum"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var put = _server.Requests.Should().Contain(r => r.Method == "PUT").Subject;
        put.PathAndQuery.Should().Be($"/api/customers/{customerId}");
        put.Body.Should().Contain("Platinum");
    }

    [Fact]
    public void Transactional_FailsValidation_WithAClearMessage()
    {
        var settings = OrdersApiSettings(LifecycleMode.Transactional);
        var runtime = BuildRuntime(settings);

        // Surfaced pre-persistence, so `tdm validate` refuses before any HTTP traffic…
        runtime.PolicyViolations.Should().ContainSingle().Which
            .Should().Contain("Transactional lifecycle is unsupported").And.Contain("Persistent or TrackedTeardown");

        // …and the runtime guards direct use as a backstop.
        FluentActions.Awaiting(() => runtime.BeginScenarioAsync(LifecycleMode.Transactional, 1, Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transactional*unsupported*");
    }

    [Fact]
    public async Task QuerySteps_FailActionably_DeleteAllAndCounts()
    {
        var settings = OrdersApiSettings(LifecycleMode.Persistent);
        await using var runtime = BuildRuntime(settings);
        runtime.TryResolveEntity("Order", out var order, out _).Should().BeTrue();

        await FluentActions.Awaiting(() => runtime.DeleteWhereAsync(order!, [], Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no*query surface*");
        await FluentActions.Awaiting(() => runtime.CountAsync(order!, [], Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*count verification*");
    }

    [Fact]
    public async Task ServerErrors_AreRetried_ThenSurfaced()
    {
        var failures = 0;
        _server.OnRequest = request =>
        {
            if (request.Method == "POST" && Interlocked.Increment(ref failures) == 1)
                return (503, null); // first attempt fails, retry succeeds
            return request.Method == "POST" ? (201, request.Body) : (404, null);
        };

        var settings = OrdersApiSettings(LifecycleMode.Persistent);
        await using var runtime = BuildRuntime(settings);
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Customer exists with name "Retry Ltd"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        _server.Requests.Count(r => r.Method == "POST").Should().Be(2);
    }
}
