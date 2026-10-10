using CloudHealthOffice.Infrastructure.Edi.Interchange;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Wraps the ERA envelope repository so every 835 that payment and reversal
/// runs persist is also recorded as an outbound interchange (ISA13, sender,
/// receiver; no content), which the trading partner's TA1 is matched to.
/// Tracking never fails the write: <see cref="IOutboundInterchangeTracker.RecordSentAsync"/>
/// logs and swallows its own errors.
/// </summary>
public sealed class TrackingEraEnvelopeRepository : IEraEnvelopeRepository
{
    private readonly IEraEnvelopeRepository _inner;
    private readonly IOutboundInterchangeTracker _tracker;

    public TrackingEraEnvelopeRepository(IEraEnvelopeRepository inner, IOutboundInterchangeTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    public async Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record)
    {
        var created = await _inner.CreateAsync(record);
        await _tracker.RecordSentAsync(created.TenantId, created.EdiContent, "835", created.Id);
        return created;
    }

    public Task<EraEnvelopeRecord?> GetByIdAsync(string id) => _inner.GetByIdAsync(id);
    public Task<IEnumerable<EraEnvelopeRecord>> GetByPaymentRunIdAsync(string paymentRunId) => _inner.GetByPaymentRunIdAsync(paymentRunId);
    public Task<IEnumerable<EraEnvelopeRecord>> GetByReversalRunIdAsync(string reversalRunId) => _inner.GetByReversalRunIdAsync(reversalRunId);
    public Task<IEnumerable<EraEnvelopeRecord>> SearchAsync(string? paymentRunId, string? tradingPartnerId, string? reversalRunId = null) =>
        _inner.SearchAsync(paymentRunId, tradingPartnerId, reversalRunId);
    public Task<IReadOnlyCollection<string>> GetClaimIdsWithEnvelopeAsync(IReadOnlyCollection<string> claimIds, bool reversal) =>
        _inner.GetClaimIdsWithEnvelopeAsync(claimIds, reversal);

    /// <summary>
    /// Replaces the registered <see cref="IEraEnvelopeRepository"/> with this
    /// wrapper around it. The inner registration keeps its lifetime (the
    /// in-memory fallback stays a singleton); the wrapper is scoped, like the tracker.
    /// </summary>
    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IEraEnvelopeRepository));
        if (descriptor?.ImplementationType is not { } implementation) return;

        services.Remove(descriptor);
        services.Add(new ServiceDescriptor(implementation, implementation, descriptor.Lifetime));
        services.AddScoped<IEraEnvelopeRepository>(sp => new TrackingEraEnvelopeRepository(
            (IEraEnvelopeRepository)sp.GetRequiredService(implementation),
            sp.GetRequiredService<IOutboundInterchangeTracker>()));
    }
}
