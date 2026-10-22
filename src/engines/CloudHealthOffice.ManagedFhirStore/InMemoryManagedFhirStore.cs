using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.ManagedFhirStore;

/// <summary>
/// <see cref="IManagedFhirStore"/> held in process memory, for Development
/// and tests. Refused outside Development by
/// <see cref="ManagedFhirStoreServiceCollectionExtensions"/>.
/// </summary>
public sealed class InMemoryManagedFhirStore : IManagedFhirStore
{
    private readonly ConcurrentDictionary<(string Type, string Id), (string Json, int Version)> _resources = new();

    public string ProviderName => "in-memory";

    public int Count => _resources.Count;

    public Task<ManagedFhirWriteResult> UpsertAsync(string resourceType, string id, string resourceJson, CancellationToken ct = default)
    {
        GcpHealthcareFhirStore.ValidateKey(resourceType, id);
        var node = JsonNode.Parse(resourceJson);
        if (node?["resourceType"]?.GetValue<string>() != resourceType || node["id"]?.GetValue<string>() != id)
        {
            throw new ArgumentException("resourceType and id in the body must match the key being written.", nameof(resourceJson));
        }

        var stored = _resources.AddOrUpdate(
            (resourceType, id),
            _ => (resourceJson, 1),
            (_, existing) => (resourceJson, existing.Version + 1));
        return Task.FromResult(new ManagedFhirWriteResult(
            stored.Version == 1 ? 201 : 200, stored.Version.ToString(CultureInfo.InvariantCulture), null));
    }

    public Task<string?> ReadAsync(string resourceType, string id, CancellationToken ct = default) =>
        Task.FromResult(_resources.TryGetValue((resourceType, id), out var stored) ? stored.Json : null);

    public Task DeleteAsync(string resourceType, string id, CancellationToken ct = default)
    {
        _resources.TryRemove((resourceType, id), out _);
        return Task.CompletedTask;
    }

    public Task<ManagedFhirExportJob> StartExportAsync(ManagedFhirExportRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException("The in-memory managed FHIR store does not export.");

    public Task<ManagedFhirExportJob> GetExportStatusAsync(string jobId, CancellationToken ct = default) =>
        throw new NotSupportedException("The in-memory managed FHIR store does not export.");
}
