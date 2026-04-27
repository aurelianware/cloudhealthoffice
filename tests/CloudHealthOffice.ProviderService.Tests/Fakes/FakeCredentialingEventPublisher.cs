using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ICredentialingEventPublisher"/> that mirrors the
/// production semantics needed by service-level tests: deterministic
/// EventId-based idempotency, monotonic Version per (TenantId, ProviderId),
/// PartitionKey + Mongo-style _id assignment. Wraps an
/// <see cref="InMemoryCredentialingEventRepository"/> so a single store
/// is shared with the service's read-side dependency.
/// </summary>
public sealed class FakeCredentialingEventPublisher : ICredentialingEventPublisher
{
    private readonly InMemoryCredentialingEventRepository _repository;

    public FakeCredentialingEventPublisher(InMemoryCredentialingEventRepository repository)
    {
        _repository = repository;
    }

    public Task<CredentialingEvent> PublishAsync(CredentialingEvent evt, CancellationToken ct = default)
    {
        evt.PartitionKey = CredentialingEvent.BuildPartitionKey(evt.TenantId, evt.ProviderId);
        evt.Id = $"{evt.PartitionKey}:{evt.EventId}";
        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;

        var existing = _repository.Store.FirstOrDefault(e =>
            e.TenantId == evt.TenantId && e.ProviderId == evt.ProviderId && e.EventId == evt.EventId);
        if (existing != null) return Task.FromResult(existing);

        var nextVersion = _repository.Store
            .Where(e => e.TenantId == evt.TenantId && e.ProviderId == evt.ProviderId)
            .Select(e => e.Version)
            .DefaultIfEmpty(0)
            .Max() + 1;
        evt.Version = nextVersion;
        _repository.Store.Add(evt);
        return Task.FromResult(evt);
    }
}
