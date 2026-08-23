using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Adapters;

/// <summary>
/// Direct canonical inbound adapter. Passes
/// <see cref="PayerEligibilityInquiry"/> to
/// <see cref="IEligibilityResponder"/> unchanged. No vendor mapping.
/// </summary>
public sealed class CanonicalInboundEligibilityAdapter : ICanonicalInboundEligibilityAdapter
{
    public const string AdapterName = "canonical";

    private readonly IEligibilityResponder _responder;

    public CanonicalInboundEligibilityAdapter(IEligibilityResponder responder)
    {
        _responder = responder;
    }

    public string Name => AdapterName;

    public bool IsImplemented => true;

    public Task<GatewayResponse<PayerEligibilityResponse>> ProcessAsync(
        PayerEligibilityInquiry inquiry,
        CancellationToken ct = default)
    {
        inquiry.AdapterName ??= AdapterName;
        inquiry.SourceMetadata ??= new PayerEligibilitySourceMetadata { Network = AdapterName };
        return _responder.RespondAsync(inquiry, ct);
    }
}
