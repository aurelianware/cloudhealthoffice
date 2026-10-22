using CloudHealthOffice.Infrastructure.Edi.Interchange;
using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Wraps the ERA envelope repository so every 835 that payment and reversal
/// runs persist gets its sequenced control numbers before it is stored, and is
/// recorded as an outbound interchange and EDI file (identifiers only; no
/// content), which the trading partner's TA1 is matched to.
/// Tracking never fails the write: <see cref="IOutboundInterchangeTracker.RecordSentAsync"/>
/// logs and swallows its own errors.
/// </summary>
public sealed class TrackingEraEnvelopeRepository : IEraEnvelopeRepository
{
    private readonly IEraEnvelopeRepository _inner;
    private readonly IOutboundInterchangeTracker _tracker;
    private readonly IX12ControlNumberAllocator _allocator;

    public TrackingEraEnvelopeRepository(IEraEnvelopeRepository inner, IOutboundInterchangeTracker tracker, IX12ControlNumberAllocator allocator)
    {
        _inner = inner;
        _tracker = tracker;
        _allocator = allocator;
    }

    public async Task<EraEnvelopeRecord> CreateAsync(EraEnvelopeRecord record)
    {
        // The batch generator's numbers are placeholders: assign the sequenced
        // ISA13/GS06/ST02 before the envelope is stored, so what is stored is
        // what goes out (edi-operations.md §5). A failure to assign is thrown.
        var assignment = await _allocator.AssignAsync(record.TenantId, record.EdiContent);
        if (assignment.Interchanges.Count > 0)
        {
            record.EdiContent = assignment.Content;
            record.ControlNumber = assignment.Interchanges[0].InterchangeControlNumber;
        }
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
            sp.GetRequiredService<IOutboundInterchangeTracker>(),
            sp.GetRequiredService<IX12ControlNumberAllocator>()));
    }
}
