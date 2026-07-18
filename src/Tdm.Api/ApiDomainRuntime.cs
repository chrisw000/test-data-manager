using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bogus;
using Microsoft.Extensions.Logging;
using Tdm.Core.Conversion;
using Tdm.Core.Execution;
using Tdm.Core.Generation;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Api;

/// <summary>The build-once, immutable half of an API domain — shared by all sessions (W3-D2).</summary>
internal sealed record ApiRuntimeCore(
    DomainSettings Settings,
    TdmSettings Root,
    List<ApiEntityBinding> Bindings,
    List<string> Warnings,
    List<string> PolicyViolations,
    IReadOnlyList<IValueGeneratorPlugin> GeneratorPlugins,
    StatisticalGenerator Stats,
    HttpClient Http,
    ILogger? Log);

/// <summary>
/// API-backed <see cref="IDomainRuntime"/> (W4-D6): persistence routes through the domain's
/// public HTTP API — validation, auth and side-effects exercised for free. The engine is
/// untouched; generation (fakers, plugins, statistical layer, identity) is byte-identical to
/// the EF runtime because it is the same shared machinery. Lifecycles: Persistent and
/// TrackedTeardown (deletes via API, reverse order); Transactional fails validation.
/// </summary>
public sealed class ApiDomainRuntime : IDomainRuntime
{
    /// <summary>Wire contract for payloads/responses: camelCase, enums as strings, case-insensitive reads.</summary>
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ApiRuntimeCore _core;

    // Per-scenario state.
    private readonly Dictionary<Type, object> _fakerInstances = [];
    private readonly List<(ApiEntityBinding Binding, object Instance)> _tracked = [];
    private Faker _autoFaker = new();
    private LifecycleMode _lifecycle = LifecycleMode.Persistent;
    private int _seedValue;

    internal ApiDomainRuntime(ApiRuntimeCore core)
    {
        _core = core;
        Entities = core.Bindings.Select(b => b.Descriptor).ToList();
    }

    public string Name => _core.Settings.Name;
    public DomainSettings Settings => _core.Settings;
    public IReadOnlyList<EntityDescriptor> Entities { get; }
    public IReadOnlyList<string> Warnings => _core.Warnings;
    public IReadOnlyList<string> PolicyViolations => _core.PolicyViolations;

    public IDomainRuntime CreateSession() => new ApiDomainRuntime(_core);

    // ---------------------------------------------------------------- Resolution

    public bool TryResolveEntity(string gherkinName, out EntityDescriptor? entity, out string? error)
    {
        error = null;
        entity = _core.Bindings.FirstOrDefault(b => NameMatcher.Matches(gherkinName, b.Descriptor.LogicalName))
            ?.Descriptor;
        return entity is not null;
    }

    public IReadOnlyList<EntityResolutionInfo> DescribeEntities() =>
        _core.Bindings.Select(b => new EntityResolutionInfo(
            b.Descriptor.LogicalName,
            b.Descriptor.ClrType.FullName ?? b.Descriptor.ClrType.Name,
            b.Descriptor.NaturalKeyProperty?.Name,
            b.Descriptor.KeyProperty is null
                ? "(no single-column key)"
                : $"{b.Descriptor.KeyProperty.Name}:{b.Descriptor.KeyProperty.PropertyType.Name}" +
                  (b.Descriptor.KeyIsDbGenerated ? " (server-assigned)" : $" ({b.Descriptor.IdStrategy})"),
            null,
            null,
            b.Faker?.FakerType.Name ?? "auto",
            b.Endpoints.Create ?? "(no create endpoint)")).ToList();

    private ApiEntityBinding BindingFor(EntityDescriptor descriptor) =>
        _core.Bindings.First(b => ReferenceEquals(b.Descriptor, descriptor) ||
                                  b.Descriptor.LogicalName == descriptor.LogicalName);

    // ---------------------------------------------------------------- Lifecycle

    public Task BeginScenarioAsync(LifecycleMode lifecycle, int seed, CancellationToken ct = default)
    {
        if (lifecycle == LifecycleMode.Transactional)
        {
            throw new InvalidOperationException(
                $"Domain '{Name}' uses persistence 'Api' — the Transactional lifecycle is unsupported " +
                "(there is no cross-API transaction). Use Persistent or TrackedTeardown.");
        }
        _lifecycle = lifecycle;
        _seedValue = seed;
        _autoFaker = CreateLocaleFaker(_core.Settings.Locale, seed);
        _fakerInstances.Clear();
        _tracked.Clear();
        return Task.CompletedTask;
    }

    public async Task<ScenarioCloseOutcome> EndScenarioAsync(CancellationToken ct = default)
    {
        var outcome = new ScenarioCloseOutcome();
        if (_lifecycle == LifecycleMode.TrackedTeardown)
        {
            // Reverse creation order — children before principals, deleted via the API (W4-D6).
            for (var i = _tracked.Count - 1; i >= 0; i--)
            {
                var (binding, instance) = _tracked[i];
                try
                {
                    var result = await DeleteCoreAsync(binding, instance, ct).ConfigureAwait(false);
                    if (result.Success) outcome.Deleted++;
                    else outcome.Orphaned.Add($"{Name}.{binding.Descriptor.LogicalName}: {result.Error}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcome.Orphaned.Add($"{Name}.{binding.Descriptor.LogicalName}: {ex.Message}");
                }
            }
        }
        _tracked.Clear();
        _fakerInstances.Clear();
        return outcome;
    }

    private static Faker CreateLocaleFaker(string? locale, int seed)
    {
        try
        {
            return new Faker(locale ?? "en") { Random = new Randomizer(seed) };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                $"'{locale}' is not a valid Bogus locale. Examples: en, en_GB, de, fr, es, it, nl, pl, sv, ja, pt_BR, zh_CN.", ex);
        }
    }

    // ---------------------------------------------------------------- Generation

    public object Generate(EntityDescriptor entity, out string fakerSource, List<string> warnings)
    {
        var binding = BindingFor(entity);
        object instance;
        string baseSource;
        var usedPlugins = new List<string>();
        if (binding.Faker is { } faker)
        {
            if (!_fakerInstances.TryGetValue(faker.FakerType, out var fakerInstance))
            {
                fakerInstance = faker.CreateSeeded(_seedValue);
                _fakerInstances[faker.FakerType] = fakerInstance;
            }
            baseSource = faker.FakerType.Name;
            instance = faker.Generate(fakerInstance);
        }
        else
        {
            baseSource = "auto";
            instance = AutoFaker.Generate(entity, binding.AutoFakerSkip, _autoFaker,
                _core.GeneratorPlugins, _core.Settings.Locale, usedPlugins);
        }

        var stat = _core.Stats.Apply(entity, instance, _autoFaker.Random);
        fakerSource = baseSource
            + (usedPlugins.Count > 0 ? $"+plugin:{string.Join(",", usedPlugins)}" : "")
            + (stat.Distributions ? "+distributions" : "")
            + (stat.Datasets ? "+datasets" : "");
        return instance;
    }

    // ---------------------------------------------------------------- Persistence

    public async Task<PersistOutcome> CreateAsync(EntityDescriptor entity, object instance,
        bool forceDbContext = false, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (binding.Endpoints.Create is not { } template)
        {
            return PersistOutcome.Fail(
                $"api.entities.{entity.LogicalName} has no 'create' endpoint configured.");
        }

        var (method, path) = Substitute(template, binding, instance);
        var response = await SendAsync(method, path, SerializeScalars(binding, instance), ct).ConfigureAwait(false);
        if (!response.Success) return PersistOutcome.Fail(response.Error!, template);

        // Server-assigned ids come back in the response — captured into the instance so the
        // manifest (and teardown) record them, exactly like DB-generated ints (W4-D6).
        if (entity.KeyIsDbGenerated && entity.KeyProperty is not null)
            TryCaptureServerAssignedId(entity, instance, response.Body);

        if (_lifecycle == LifecycleMode.TrackedTeardown) _tracked.Add((binding, instance));
        return PersistOutcome.Ok(template);
    }

    /// <summary>Bulk rides the same per-row endpoint — there is no cross-API batch. The W2/W3
    /// policy gates (maxBulkRowsPerStep) are the intended guard for volume via API (§6 risk).</summary>
    public async Task<PersistOutcome> CreateBulkAsync(EntityDescriptor entity, IReadOnlyList<object> instances,
        BulkPersistOptions options, CancellationToken ct = default)
    {
        for (var i = 0; i < instances.Count; i++)
        {
            var outcome = await CreateAsync(entity, instances[i], forceDbContext: false, ct).ConfigureAwait(false);
            if (!outcome.Success)
                return PersistOutcome.Fail($"row {i + 1}/{instances.Count}: {outcome.Error}", outcome.Route);
        }
        return PersistOutcome.Ok($"{BindingFor(entity).Endpoints.Create} × {instances.Count} (per-row)");
    }

    public async Task<PersistOutcome> UpdateAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (binding.Endpoints.Update is not { } template)
            return PersistOutcome.Fail($"api.entities.{entity.LogicalName} has no 'update' endpoint configured.");

        var (method, path) = Substitute(template, binding, instance);
        var response = await SendAsync(method, path, SerializeScalars(binding, instance), ct).ConfigureAwait(false);
        return response.Success ? PersistOutcome.Ok(template) : PersistOutcome.Fail(response.Error!, template);
    }

    public async Task<PersistOutcome> DeleteAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        var result = await DeleteCoreAsync(BindingFor(entity), instance, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<PersistOutcome> DeleteCoreAsync(ApiEntityBinding binding, object instance, CancellationToken ct)
    {
        if (binding.Endpoints.Delete is not { } template)
            return PersistOutcome.Fail($"api.entities.{binding.Descriptor.LogicalName} has no 'delete' endpoint configured.");

        var (method, path) = Substitute(template, binding, instance);
        var response = await SendAsync(method, path, body: null, ct, notFoundIsSuccess: true).ConfigureAwait(false);
        return response.Success ? PersistOutcome.Ok(template) : PersistOutcome.Fail(response.Error!, template);
    }

    public Task<int> DeleteWhereAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Domain '{Name}' (persistence Api): filtered/delete-all steps are not supported — the API exposes no " +
            "query surface. Delete by natural key instead (\"the <Entity> \\\"<key>\\\" is deleted\").");

    public Task<int> CountAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Domain '{Name}' (persistence Api): count verification is not supported — the API exposes no query " +
            "surface. Verify single entities by natural key instead.");

    // ---------------------------------------------------------------- Reads

    public async Task<object?> FindByNaturalKeyAsync(EntityDescriptor entity, string naturalKey, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (binding.Endpoints.GetByKey is not { } template)
        {
            throw new InvalidOperationException(
                $"api.entities.{entity.LogicalName} has no 'getByKey' endpoint — required for references, " +
                "load/verify steps and idempotent create-or-reuse.");
        }
        return await GetEntityAsync(binding, template, key: naturalKey, id: null, ct).ConfigureAwait(false);
    }

    public async Task<object?> FindByIdAsync(EntityDescriptor entity, string id, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (binding.Endpoints.GetById is not { } template)
        {
            throw new InvalidOperationException(
                $"api.entities.{entity.LogicalName} has no 'getById' endpoint — configure one to enable " +
                "manifest playback/verify against this domain.");
        }
        return await GetEntityAsync(binding, template, key: null, id: id, ct).ConfigureAwait(false);
    }

    private async Task<object?> GetEntityAsync(ApiEntityBinding binding, string template,
        string? key, string? id, CancellationToken ct)
    {
        var (method, path) = ParseTemplate(template);
        if (key is not null) path = path.Replace("{key}", Uri.EscapeDataString(key), StringComparison.Ordinal);
        if (id is not null) path = path.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

        var response = await SendAsync(method, path, body: null, ct, notFoundIsNull: true).ConfigureAwait(false);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        if (response.NotFound || string.IsNullOrWhiteSpace(response.Body)) return null;

        // The endpoint may return the object or a (possibly empty) array of matches.
        using var document = JsonDocument.Parse(response.Body);
        var element = document.RootElement;
        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0) return null;
            element = element[0];
        }
        return element.Deserialize(binding.Descriptor.ClrType, WireJson);
    }

    public Task<bool> DeleteByIdAsync(string logicalEntityName, string id, CancellationToken ct = default)
    {
        var binding = _core.Bindings.FirstOrDefault(b =>
                NameMatcher.Matches(logicalEntityName, b.Descriptor.LogicalName))
            ?? throw new InvalidOperationException($"Entity '{logicalEntityName}' is not in domain '{Name}'s api.entities map.");
        if (binding.Endpoints.Delete is not { } template)
            throw new InvalidOperationException($"api.entities.{binding.Descriptor.LogicalName} has no 'delete' endpoint configured.");

        var (method, path) = ParseTemplate(template);
        path = path.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
        return DeleteByPathAsync(method, path, ct);
    }

    private async Task<bool> DeleteByPathAsync(string method, string path, CancellationToken ct)
    {
        var response = await SendAsync(method, path, body: null, ct, notFoundIsNull: true).ConfigureAwait(false);
        if (!response.Success) throw new InvalidOperationException(response.Error);
        return !response.NotFound;
    }

    // ---------------------------------------------------------------- References

    public bool TrySetReference(object instance, EntityDescriptor entity, string referencedEntityName,
        EntityDescriptor? referencedDescriptor, object? referencedInstance, object? referencedId, out string? error)
    {
        // FK by convention first ({Entity}Id), then a navigation of the referenced CLR type —
        // the same order the EF runtime falls back through when metadata is absent.
        var fk = entity.ClrType.GetProperties()
            .FirstOrDefault(p => NameMatcher.Matches($"{referencedEntityName}Id", p.Name) && p.CanWrite);
        if (fk is not null && referencedId is not null)
        {
            if (fk.PropertyType.IsInstanceOfType(referencedId))
            {
                fk.SetValue(instance, referencedId);
                error = null;
                return true;
            }
            var raw = Convert.ToString(referencedId, CultureInfo.InvariantCulture) ?? "";
            if (ValueConverter.TryConvert(raw, fk.PropertyType, out var converted, out var convertError))
            {
                fk.SetValue(instance, converted);
                error = null;
                return true;
            }
            error = $"cannot convert reference id '{raw}' to {fk.PropertyType.Name} for {entity.LogicalName}.{fk.Name}: {convertError}";
            return false;
        }

        if (referencedDescriptor is not null && referencedInstance is not null)
        {
            var navigation = entity.ClrType.GetProperties().FirstOrDefault(p =>
                p.CanWrite && p.PropertyType.IsAssignableFrom(referencedDescriptor.ClrType));
            if (navigation is not null)
            {
                navigation.SetValue(instance, referencedInstance);
                error = null;
                return true;
            }
        }

        error = $"{entity.ClrType.Name} has no '{referencedEntityName}Id' property or navigation to set the reference.";
        return false;
    }

    // ---------------------------------------------------------------- HTTP plumbing

    private sealed record ApiResponse(bool Success, bool NotFound, string? Body, string? Error);

    /// <summary>Substitutes {id} (key value) and {key} (natural key) from the instance.</summary>
    private static (string Method, string Path) Substitute(string template, ApiEntityBinding binding, object instance)
    {
        var (method, path) = ParseTemplate(template);
        if (path.Contains("{id}", StringComparison.Ordinal))
        {
            var id = Convert.ToString(binding.Descriptor.GetKey(instance), CultureInfo.InvariantCulture) ?? "";
            path = path.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
        }
        if (path.Contains("{key}", StringComparison.Ordinal))
        {
            var key = binding.Descriptor.GetNaturalKey(instance) ?? "";
            path = path.Replace("{key}", Uri.EscapeDataString(key), StringComparison.Ordinal);
        }
        return (method, path);
    }

    private static (string Method, string Path) ParseTemplate(string template)
    {
        var separator = template.IndexOf(' ');
        return (template[..separator].ToUpperInvariant(), template[(separator + 1)..].Trim());
    }

    /// <summary>Scalar properties only (navigations excluded) — no cycles, and the payload
    /// mirrors what the manifest snapshots. Server-assigned keys are omitted.</summary>
    private static string SerializeScalars(ApiEntityBinding binding, object instance)
    {
        var payload = new Dictionary<string, object?>();
        foreach (var property in binding.Descriptor.ScalarProperties())
        {
            if (binding.Descriptor.KeyIsDbGenerated &&
                property.Name == binding.Descriptor.KeyProperty?.Name) continue;
            payload[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(instance);
        }
        return JsonSerializer.Serialize(payload, WireJson);
    }

    private void TryCaptureServerAssignedId(EntityDescriptor entity, object instance, string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, entity.KeyProperty!.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var value = property.Value.Deserialize(entity.KeyProperty.PropertyType, WireJson);
                if (value is not null) entity.SetKey(instance, value);
                return;
            }
        }
        catch (JsonException)
        {
            // Non-JSON response body — id stays unknown; the manifest records null.
        }
    }

    /// <summary>One request with bounded retries on connection failure/5xx (§6 risk table).</summary>
    private async Task<ApiResponse> SendAsync(string method, string path, string? body, CancellationToken ct,
        bool notFoundIsSuccess = false, bool notFoundIsNull = false)
    {
        var api = _core.Settings.Api!;
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Parse(method), path.TrimStart('/'));
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await _core.Http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < api.MaxRetries)
            {
                _core.Log?.LogWarning("{Domain}: {Method} {Path} failed ({Error}) — retry {Attempt}/{Max}",
                    Name, method, path, ex.Message, attempt + 1, api.MaxRetries);
                await Task.Delay(api.RetryDelayMs, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                return new ApiResponse(false, false, null,
                    $"{method} {path}: {ex.Message} (after {api.MaxRetries + 1} attempt(s))");
            }

            using (response)
            {
                var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return new ApiResponse(true, false, content, null);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && (notFoundIsSuccess || notFoundIsNull))
                    return new ApiResponse(true, true, null, null);

                if ((int)response.StatusCode >= 500 && attempt < api.MaxRetries)
                {
                    _core.Log?.LogWarning("{Domain}: {Method} {Path} returned {Status} — retry {Attempt}/{Max}",
                        Name, method, path, (int)response.StatusCode, attempt + 1, api.MaxRetries);
                    await Task.Delay(api.RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }
                return new ApiResponse(false, false, content,
                    $"{method} {path} → {(int)response.StatusCode} {response.ReasonPhrase}" +
                    (string.IsNullOrWhiteSpace(content) ? "" : $": {Truncate(content)}"));
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";

    public ValueTask DisposeAsync()
    {
        // The HttpClient lives on the shared core — sessions must not dispose it. The root
        // runtime's client is process-lifetime, reclaimed on unload like EF's pooled services.
        _tracked.Clear();
        _fakerInstances.Clear();
        return ValueTask.CompletedTask;
    }
}
