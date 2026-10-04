using System.Collections.Concurrent;
using CloudHealthOffice.TradingPartnerService.Controllers;
using CloudHealthOffice.TradingPartnerService.Models;
using CloudHealthOffice.TradingPartnerService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.TradingPartnerService.Tests.Security;

/// <summary>
/// The real trading-partner-service pipeline (Development environment, the
/// development ChoAuth block from appsettings.Development.json) with the
/// repository replaced by an in-memory one.
/// </summary>
public sealed class TradingPartnerPipelineFactory : WebApplicationFactory<TradingPartnersController>
{
    public InMemoryTradingPartnerRepository Repository { get; private set; } = new();

    public void Reset() => Repository = new InMemoryTradingPartnerRepository();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("MongoDb:ConnectionString", "");
        builder.UseSetting("Database:Provider", "");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITradingPartnerRepository>();
            services.AddScoped<ITradingPartnerRepository>(_ => Repository);
        });
    }
}

/// <summary>Keyed like the Mongo repository: (tenant, id).</summary>
public sealed class InMemoryTradingPartnerRepository : ITradingPartnerRepository
{
    private readonly ConcurrentDictionary<(string Tenant, string Id), TradingPartner> _items = new();
    public ConcurrentQueue<string> Calls { get; } = new();

    public IReadOnlyCollection<TradingPartner> All => _items.Values.ToList();

    public TradingPartner? Find(string tenantId, string tradingPartnerId, string environment)
        => _items.TryGetValue((tenantId, $"{tradingPartnerId}-{tenantId}-{environment}"), out var p) ? p : null;

    public void Seed(TradingPartner partner) => _items[(partner.TenantId, partner.Id)] = partner;

    public Task<TradingPartner?> GetAsync(string tenantId, string tradingPartnerId, string environment)
    {
        Calls.Enqueue($"get:{tenantId}");
        return Task.FromResult(Find(tenantId, tradingPartnerId, environment));
    }

    public Task<IEnumerable<TradingPartner>> GetByTenantAsync(string tenantId)
    {
        Calls.Enqueue($"list:{tenantId}");
        return Task.FromResult<IEnumerable<TradingPartner>>(_items.Values.Where(p => p.TenantId == tenantId).ToList());
    }

    public Task<TradingPartner> CreateAsync(TradingPartner partner)
    {
        Calls.Enqueue($"create:{partner.TenantId}");
        _items[(partner.TenantId, partner.Id)] = partner;
        return Task.FromResult(partner);
    }

    public Task<TradingPartner> UpdateAsync(TradingPartner partner)
    {
        Calls.Enqueue($"update:{partner.TenantId}");
        _items[(partner.TenantId, partner.Id)] = partner;
        return Task.FromResult(partner);
    }

    public Task DeleteAsync(string id, string partitionKey)
    {
        Calls.Enqueue($"delete:{partitionKey}");
        _items.TryRemove((partitionKey, id), out _);
        return Task.CompletedTask;
    }
}
