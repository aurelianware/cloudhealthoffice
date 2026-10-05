using System.Net.Http.Json;
using CapitationService.Services;

namespace CapitationService.Tests.Support;

/// <summary>
/// Unit-test stand-in for provider-service's full-number read: answers from the
/// same mocked "ProviderService" client the masked read uses, so a test sets up
/// one bank account for both. Found only with EFT enabled and both numbers,
/// otherwise unavailable (needs attention), as <see cref="HttpProviderBankAccountSource"/>.
/// Records each lookup.
/// </summary>
public sealed class FactoryBackedProviderBankAccountSource : IProviderBankAccountSource
{
    private readonly IHttpClientFactory _factory;

    public FactoryBackedProviderBankAccountSource(IHttpClientFactory factory) => _factory = factory;

    public List<(string Tenant, string Npi)> Lookups { get; } = new();

    public async Task<ProviderBankAccountLookup> GetForDisbursementAsync(string tenantId, string providerNpi, CancellationToken cancellationToken = default)
    {
        Lookups.Add((tenantId, providerNpi));
        try
        {
            var client = _factory.CreateClient("ProviderService");
            using var response = await client.GetAsync($"/api/v1/internal/providers/npi/{providerNpi}/bank-account", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ProviderBankAccountLookup.Unavailable($"Provider {providerNpi} has no approved bank account. Needs attention.");
            var account = await response.Content.ReadFromJsonAsync<ProviderBankAccountDto>(cancellationToken: cancellationToken);
            return account is { EftEnabled: true, RoutingNumber.Length: > 0, AccountNumber.Length: > 0 }
                ? ProviderBankAccountLookup.Of(account)
                : ProviderBankAccountLookup.Unavailable($"Provider {providerNpi} has no EFT routing and account numbers. Needs attention.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProviderBankAccountLookup.Unavailable($"Lookup failed ({ex.GetType().Name}). Needs attention.");
        }
    }
}
