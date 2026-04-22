using System.Collections.Concurrent;
using ConsentService.Models;
using ConsentService.Services;

namespace ConsentService.Tests.Fakes;

/// <summary>
/// Captures every <c>PublishStatusChangedAsync</c> call so controller tests
/// can assert "one event published, exactly these fields, in this order".
/// </summary>
public sealed class RecordingConsentEventPublisher : IConsentEventPublisher
{
    public readonly ConcurrentQueue<PublishedCall> Calls = new();

    public Task PublishStatusChangedAsync(
        Consent consent,
        ConsentStatus? fromStatus,
        ConsentStatus toStatus,
        string actor,
        string? correlationId,
        CancellationToken ct = default)
    {
        Calls.Enqueue(new PublishedCall(
            ConsentId: consent.Id,
            TenantId: consent.TenantId,
            MemberId: consent.MemberId,
            FromStatus: fromStatus,
            ToStatus: toStatus,
            Actor: actor,
            CorrelationId: correlationId));
        return Task.CompletedTask;
    }

    public sealed record PublishedCall(
        string ConsentId,
        string TenantId,
        string MemberId,
        ConsentStatus? FromStatus,
        ConsentStatus ToStatus,
        string Actor,
        string? CorrelationId);
}
