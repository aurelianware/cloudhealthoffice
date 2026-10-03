using System.Collections.Concurrent;
using AttachmentService.Controllers;
using AttachmentService.Models;
using AttachmentService.Repositories;
using AttachmentService.Services;
using CloudHealthOffice.DocumentStore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AttachmentService.Tests.Support;

/// <summary>
/// The real attachment-service pipeline (authentication, tenant middleware,
/// permission policies, controllers) over in-memory storage.
/// </summary>
// Program is a top-level (internal) class, so the entry point is named through a controller type.
public sealed class AttachmentServiceFactory : WebApplicationFactory<AttachmentsController>
{
    public InMemoryAttachmentRepository Attachments { get; } = new();
    public InMemoryDocumentStore Documents { get; } = new();
    public Mock<ITradingPartnerLookup> TradingPartners { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAttachmentRepository>();
            services.AddSingleton<IAttachmentRepository>(Attachments);
            services.RemoveAll<IDocumentStore>();
            services.AddSingleton<IDocumentStore>(Documents);
            services.RemoveAll<ITradingPartnerLookup>();
            services.AddSingleton(TradingPartners.Object);
        });
    }

    public void Reset()
    {
        Attachments.Items.Clear();
        Documents.Clear();
        TradingPartners.Reset();
    }
}

public sealed class InMemoryAttachmentRepository : IAttachmentRepository
{
    public ConcurrentDictionary<string, Attachment> Items { get; } = new();

    public Attachment Seed(string tenantId, string? claimId = "CLM-1", string? id = null)
    {
        var attachment = new Attachment
        {
            Id = id ?? Guid.NewGuid().ToString(),
            TenantId = tenantId,
            ClaimId = claimId,
            PayerId = "PAYER-1",
            PayerName = "Payer",
            ProviderId = "PROV-1",
            SubscriberId = "SUB-1",
            DocumentType = "Medical Records",
            DocumentFormat = "PDF",
        };
        Items[attachment.Id] = attachment;
        return attachment;
    }

    public Task<Attachment> CreateAsync(Attachment attachment)
    {
        Items[attachment.Id] = attachment;
        return Task.FromResult(attachment);
    }

    public Task<Attachment?> GetByIdAsync(string id, string tenantId)
        => Task.FromResult(Items.TryGetValue(id, out var a) && a.TenantId == tenantId ? a : null);

    public Task<IEnumerable<Attachment>> GetByClaimIdAsync(string claimId, string tenantId)
        => Task.FromResult(Items.Values.Where(a => a.TenantId == tenantId && a.ClaimId == claimId));

    public Task<IEnumerable<Attachment>> GetByAuthorizationIdAsync(string authorizationId, string tenantId)
        => Task.FromResult(Items.Values.Where(a => a.TenantId == tenantId && a.AuthorizationId == authorizationId));

    public Task<IEnumerable<Attachment>> GetByAppealIdAsync(string appealId, string tenantId)
        => Task.FromResult(Items.Values.Where(a => a.TenantId == tenantId && a.AppealId == appealId));

    public Task<Attachment?> GetByRFAIReferenceAsync(string rfaiReference, string tenantId)
        => Task.FromResult(Items.Values.FirstOrDefault(a => a.TenantId == tenantId && a.RFAIReference == rfaiReference));

    public Task<Attachment> UpdateAsync(Attachment attachment)
    {
        Items[attachment.Id] = attachment;
        return Task.FromResult(attachment);
    }

    public Task DeleteAsync(string id, string tenantId)
    {
        if (Items.TryGetValue(id, out var a) && a.TenantId == tenantId)
            Items.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
