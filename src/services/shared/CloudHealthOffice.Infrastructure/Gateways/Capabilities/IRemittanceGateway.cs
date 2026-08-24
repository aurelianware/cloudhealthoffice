using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Capabilities;

/// <summary>
/// Gateway capability for retrieving an 835 electronic remittance advice.
///
/// Stedi delivers 835s asynchronously: a webhook or poll item is a pointer,
/// and this method retrieves the normalized remittance. Applying it to claims
/// is <see cref="IRemittanceProcessor"/> — not this interface. Retrieval does
/// not post payment, update 277CA, or change 276/277 claim status.
/// </summary>
public interface IRemittanceGateway : IHealthcareTransactionGateway
{
    Task<GatewayResponse<GatewayRemittance>> RetrieveRemittanceAsync(
        RemittanceRetrievalRequest request,
        CancellationToken cancellationToken = default);
}
