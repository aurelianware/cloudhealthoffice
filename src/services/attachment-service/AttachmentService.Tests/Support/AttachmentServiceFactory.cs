using System.Collections.Concurrent;
using System.Net;
using AttachmentService.Controllers;
using AttachmentService.Models;
using AttachmentService.Repositories;
using AttachmentService.Services;
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
    public FakeDocumentCatalog Documents { get; } = new();
    public Mock<ITradingPartnerLookup> TradingPartners { get; } = new();
    public StubParentServices Parents { get; } = new();

    /// <summary>A MongoDB address that is never listening; the driver connects lazily.</summary>
    public const string UnreachableMongo =
        "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program.cs requires MongoDB in every environment and reads the
        // setting while registering services, so it goes in as a host setting.
        // Nothing reaches this server: the repository is replaced below.
        builder.UseSetting("MongoDb:ConnectionString", UnreachableMongo);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAttachmentRepository>();
            services.AddSingleton<IAttachmentRepository>(Attachments);
            services.RemoveAll<IDocumentCatalog>();
            services.AddSingleton<IDocumentCatalog>(Documents);
            services.RemoveAll<ITradingPartnerLookup>();
            services.AddSingleton(TradingPartners.Object);
            // The real HttpAttachmentParentLookup, talking to stand-ins for the owning services.
            foreach (var name in new[]
                     {
                         HttpAttachmentParentLookup.ClaimsClientName,
                         HttpAttachmentParentLookup.AuthorizationClientName,
                         HttpAttachmentParentLookup.AppealsClientName,
                     })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Parents);
            }
        });
    }

    public void Reset()
    {
        Attachments.Items.Clear();
        Attachments.ThrowOnCreate = null;
        Attachments.ThrowOnUpdate = null;
        Documents.Clear();
        TradingPartners.Reset();
        Parents.Reset();
    }
}

/// <summary>
/// Stands in for document-service: keeps each uploaded file with its tenant,
/// subject and the user it was filed for, and serves it back only for the
/// same tenant and subject, as the catalog does.
/// </summary>
public sealed class FakeDocumentCatalog : IDocumentCatalog
{
    public sealed record Stored(string TenantId, string OnBehalfOf, AttachmentParentType SubjectType, string SubjectId,
        string Title, string ContentType, string FileName, string Channel, IReadOnlyDictionary<string, string> ExternalIds, byte[] Bytes);

    private readonly ConcurrentDictionary<string, Stored> _files = new();

    /// <summary>Make every call fail, as an unreachable document-service would.</summary>
    public bool Fail { get; set; }

    public IReadOnlyDictionary<string, Stored> Files => _files;

    public int Count => _files.Count;

    public void Clear()
    {
        _files.Clear();
        Fail = false;
    }

    /// <summary>Test helper: a file already in the catalog.</summary>
    public string Seed(string tenantId, AttachmentParentType type, string subjectId, byte[] bytes)
    {
        var id = Guid.NewGuid().ToString("N");
        _files[id] = new Stored(tenantId, "seed", type, subjectId, "seed", "application/pdf", "seed.pdf", "Upload",
            new Dictionary<string, string>(), bytes);
        return id;
    }

    public Task<CatalogUploadResult> UploadAsync(CatalogUpload upload, CancellationToken ct = default)
    {
        if (Fail) throw new DocumentCatalogException("document-service unreachable");
        using var copy = new MemoryStream();
        upload.Content.CopyTo(copy);
        var id = Guid.NewGuid().ToString("N");
        _files[id] = new Stored(upload.TenantId, upload.OnBehalfOf, upload.SubjectType, upload.SubjectId, upload.Title,
            upload.ContentType, upload.FileName, upload.Channel, upload.ExternalIds, copy.ToArray());
        return Task.FromResult(new CatalogUploadResult(id));
    }

    public Task<Stream?> DownloadAsync(string tenantId, string onBehalfOf, string documentId, AttachmentParentType subjectType,
        string subjectId, CancellationToken ct = default)
    {
        if (Fail) throw new DocumentCatalogException("document-service unreachable");
        return Task.FromResult<Stream?>(
            _files.TryGetValue(documentId, out var f) && f.TenantId == tenantId && f.SubjectType == subjectType && f.SubjectId == subjectId
                ? new MemoryStream(f.Bytes)
                : null);
    }
}

/// <summary>
/// Stands in for claims-, authorization- and appeals-service: answers
/// <c>GET api/{claims|authorizations|appeals}/{id}</c> with 200 only for a
/// record of the tenant named by the request's X-Tenant-ID, else 404, as the
/// tenant-scoped services do. <see cref="FailWith"/> makes every call fail.
/// </summary>
public sealed class StubParentServices : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, byte> _records = new();

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
    public HttpStatusCode? FailWith { get; set; }

    public void Add(string tenantId, AttachmentParentType type, string id) => _records[Key(tenantId, Path(type), id)] = 0;

    public void Reset()
    {
        _records.Clear();
        Requests.Clear();
        FailWith = null;
    }

    private static string Path(AttachmentParentType type) => type switch
    {
        AttachmentParentType.Claim => "claims",
        AttachmentParentType.Authorization => "authorizations",
        _ => "appeals",
    };

    private static string Key(string tenant, string path, string id) => $"{tenant}|{path}|{id}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (FailWith is { } status) return Task.FromResult(new HttpResponseMessage(status));

        var tenant = request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.Single() : "";
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        var found = segments.Length == 3 && segments[0] == "api"
            && _records.ContainsKey(Key(tenant, segments[1], Uri.UnescapeDataString(segments[2])));
        return Task.FromResult(new HttpResponseMessage(found ? HttpStatusCode.OK : HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}"),
        });
    }
}

public sealed class InMemoryAttachmentRepository : IAttachmentRepository
{
    public ConcurrentDictionary<string, Attachment> Items { get; } = new();

    /// <summary>When set, <see cref="CreateAsync"/> throws it.</summary>
    public Exception? ThrowOnCreate { get; set; }

    /// <summary>When set, <see cref="UpdateAsync"/> throws it.</summary>
    public Exception? ThrowOnUpdate { get; set; }

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
        if (ThrowOnCreate is { } ex) throw ex;
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
        if (ThrowOnUpdate is { } ex) throw ex;
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
