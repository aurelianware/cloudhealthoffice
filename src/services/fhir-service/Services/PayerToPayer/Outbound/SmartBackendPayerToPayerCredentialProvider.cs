using FhirService.Services.SmartBackend;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>
/// Payer-to-Payer credentials over SMART Backend Services (G-42 outbound): the
/// endpoint's <see cref="PayerToPayerEndpoint.CredentialKey"/> names CHO's
/// registration with that payer under <c>SmartBackendClients:Clients</c>, and
/// the token comes from <see cref="ISmartBackendTokenClient"/>. No key, no
/// registration, or a refused token request means no token: the call goes
/// out without one and the peer's 401 records the exchange as Unauthorized.
/// </summary>
public sealed class SmartBackendPayerToPayerCredentialProvider : IPayerToPayerCredentialProvider
{
    private readonly ISmartBackendTokenClient _tokens;

    public SmartBackendPayerToPayerCredentialProvider(ISmartBackendTokenClient tokens) => _tokens = tokens;

    public async Task<string?> GetAccessTokenAsync(PayerToPayerEndpoint endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(endpoint.CredentialKey)) return null;
        var result = await _tokens.GetTokenAsync(endpoint.CredentialKey, ct);
        return result.Succeeded ? result.AccessToken : null;
    }

    public void Invalidate(PayerToPayerEndpoint endpoint)
    {
        if (!string.IsNullOrEmpty(endpoint.CredentialKey)) _tokens.Invalidate(endpoint.CredentialKey);
    }
}
