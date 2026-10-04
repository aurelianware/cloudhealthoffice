using System.Collections.Concurrent;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RfaiService.Controllers;
using RfaiService.Models;
using RfaiService.Repositories;

namespace RfaiService.Tests.Support;

/// <summary>
/// The real rfai-service pipeline (authentication, tenant middleware,
/// permission policies, controllers) over an in-memory repository.
/// </summary>
// Program is a top-level (internal) class, so the entry point is named through a controller type.
public sealed class RfaiServiceFactory : WebApplicationFactory<RfaiController>
{
    public InMemoryRfaiRepository Cases { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Also trust the development workload issuer, so an Argo workflow's
        // workload token (token-service's cho-workload in production) can be
        // presented.
        builder.UseSetting("ChoAuth:Issuers:2:Issuer", ChoDevelopmentAuth.WorkloadIssuer);
        builder.UseSetting("ChoAuth:Issuers:2:SymmetricKey", ChoDevelopmentAuth.SymmetricKey);
        builder.UseSetting("ChoAuth:Issuers:2:AllowWorkloadIdentity", "true");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRfaiRepository>();
            services.AddSingleton<IRfaiRepository>(Cases);
        });
    }
}

[CollectionDefinition(nameof(RfaiServiceCollection))]
public sealed class RfaiServiceCollection : ICollectionFixture<RfaiServiceFactory>
{
}

/// <summary>Stores copies, as a database would, so a response can never mutate the stored row.</summary>
public sealed class InMemoryRfaiRepository : IRfaiRepository
{
    private readonly ConcurrentDictionary<string, RfaiCase> _store = new();

    public void Clear() => _store.Clear();

    public RfaiCase? Find(string tenantId, string id)
        => _store.TryGetValue(Key(tenantId, id), out var c) ? Clone(c) : null;

    public RfaiCase Seed(string tenantId, string id = "case-1", string authNumber = "AUTH-1",
        RfaiStatus status = RfaiStatus.Open, string trackingId = "TRK-1")
    {
        var rfaiCase = new RfaiCase
        {
            Id = id,
            TenantId = tenantId,
            AuthNumber = authNumber,
            TrackingId = trackingId,
            Status = status,
            MemberId = "MBR-1",
            RequestingProviderNpi = "1234567893",
            RequestSource = RfaiRequestSources.PayerReview,
            RequestedItems = [new RequestedItem { Code = "03", Description = "Lab results" }],
            ReceivedAttachments =
            [
                new ReceivedAttachment
                {
                    SubmissionId = "sub-0",
                    AttachmentControlNumber = trackingId,
                    StorageProvider = "azure-blob",
                    StorageKey = $"{tenantId}/rfai/{id}/sub-0.pdf",
                    FileHash = "abc123",
                    SubmittedBy = "earlier-user",
                },
            ],
        };
        _store[Key(tenantId, id)] = Clone(rfaiCase);
        return rfaiCase;
    }

    public Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
        => Task.FromResult(Find(tenantId, id));

    public Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
        => Task.FromResult(_store.Values
            .Where(c => c.TenantId == tenantId && c.AuthNumber == authNumber)
            .Select(Clone).ToList());

    public Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId)
        => Task.FromResult(_store.Values
            .Where(c => c.TenantId == tenantId && c.TrackingId == trackingId)
            .Select(Clone).FirstOrDefault());

    public Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
        return Task.FromResult(Clone(rfaiCase));
    }

    public Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase)
    {
        var key = Key(rfaiCase.TenantId, rfaiCase.Id);
        if (_store.TryGetValue(key, out var existing))
            return Task.FromResult((Clone(existing), false));
        _store[key] = Clone(rfaiCase);
        return Task.FromResult((Clone(rfaiCase), true));
    }

    public Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
        return Task.FromResult(Clone(rfaiCase));
    }

    private static string Key(string tenantId, string id) => tenantId + "|" + id;

    private static RfaiCase Clone(RfaiCase c)
        => JsonSerializer.Deserialize<RfaiCase>(JsonSerializer.Serialize(c))!;
}
