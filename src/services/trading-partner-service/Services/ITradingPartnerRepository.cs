using CloudHealthOffice.TradingPartnerService.Models;

namespace CloudHealthOffice.TradingPartnerService.Services;

public interface ITradingPartnerRepository
{
    Task<TradingPartner?> GetAsync(string tenantId, string tradingPartnerId, string environment);
    Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId);
    Task<TradingPartner> CreateAsync(TradingPartner partner);
    Task<TradingPartner> UpdateAsync(TradingPartner partner);
    Task DeleteAsync(string id, string partitionKey);
}
