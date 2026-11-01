using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using ProviderContractsService.Models;

namespace ProviderContractsService.Services;

/// <summary>
/// Pushes a provider contract's denormalized fields to its capitation rate
/// configs in capitation-service.
/// </summary>
public interface ICapitationChildSync
{
    Task<CapitationChildSyncResult> SyncAsync(ProviderContract contract, CancellationToken ct = default);
}

/// <summary>
/// The outcome: how many rate configs were updated, or why the push failed
/// (<see cref="Error"/> set, nothing to assume about the children).
/// </summary>
public sealed record CapitationChildSyncResult(long RateConfigsUpdated, DateTime? SyncedAt, string? Error)
{
    public bool Succeeded => Error == null;
}

/// <summary>
/// <c>PUT api/v1/internal/capitation/contracts/{id}/parent-fields</c> on
/// capitation-service, which admits only this service's own token. The call
/// is made with the service identity for the caller's tenant
/// (<see cref="ChoOutboundTokenHandlerExtensions.UseChoServiceIdentity"/>):
/// the contracts user who asked for the sync need not hold payments:run.
/// </summary>
public sealed class HttpCapitationChildSync(IHttpClientFactory factory, ILogger<HttpCapitationChildSync> logger) : ICapitationChildSync
{
    public const string HttpClientName = "capitation-service";

    public async Task<CapitationChildSyncResult> SyncAsync(ProviderContract contract, CancellationToken ct = default)
    {
        try
        {
            var client = factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"api/v1/internal/capitation/contracts/{Uri.EscapeDataString(contract.Id)}/parent-fields")
            {
                Content = JsonContent.Create(new
                {
                    contractNumber = contract.ContractNumber,
                    providerNpi = contract.ProviderNPI,
                    providerName = contract.ProviderName,
                    lineOfBusiness = contract.LineOfBusiness.ToString()
                })
            };
            request.Headers.Add(TenantMiddleware.TenantHeaderName, contract.TenantId);
            request.UseChoServiceIdentity();

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("capitation-service answered {Status} to the child sync of contract {ContractId}",
                    (int)response.StatusCode, contract.Id);
                return new CapitationChildSyncResult(0, null, $"capitation-service answered {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<SyncResponse>(cancellationToken: ct);
            return new CapitationChildSyncResult(body?.RateConfigsUpdated ?? 0, body?.SyncedAt, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Child sync of contract {ContractId} could not reach capitation-service", contract.Id);
            return new CapitationChildSyncResult(0, null, "capitation-service could not be reached");
        }
    }

    private sealed record SyncResponse(long RateConfigsUpdated, DateTime SyncedAt);
}
