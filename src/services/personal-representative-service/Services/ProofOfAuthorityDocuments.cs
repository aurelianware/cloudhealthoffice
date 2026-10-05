using System.Net;
using System.Text.Json;

namespace PersonalRepresentativeService.Services;

/// <summary>Outcome of looking up a proof-of-authority document.</summary>
public enum ProofOfAuthorityDocumentStatus
{
    /// <summary>Found in the tenant, finalized, and linked to every member checked.</summary>
    Verified,

    /// <summary>No document with this id in the caller's tenant.</summary>
    NotFound,

    /// <summary>member-document-service returned a document that belongs to another tenant.</summary>
    WrongTenant,

    /// <summary>The document is not linked to one of the representative's members.</summary>
    NotLinkedToMember,

    /// <summary>The document's upload was never finalized, so it has no content.</summary>
    NotFinalized,

    /// <summary>member-document-service could not answer (error status, timeout, unreadable body).</summary>
    Unavailable
}

/// <param name="Status">What the lookup found.</param>
/// <param name="UnlinkedMemberId">For <see cref="ProofOfAuthorityDocumentStatus.NotLinkedToMember"/>: the first member the document is not linked to.</param>
public sealed record ProofOfAuthorityDocumentCheck(
    ProofOfAuthorityDocumentStatus Status,
    string? UnlinkedMemberId = null);

/// <summary>
/// Looks up a proof-of-authority document (guardianship order, power of
/// attorney, surrogate designation) in member-document-service. Only the
/// document's metadata is read: id, tenant, member links and upload state.
/// The content is never fetched.
/// </summary>
public interface IProofOfAuthorityDocuments
{
    /// <summary>
    /// Whether <paramref name="documentId"/> exists in <paramref name="tenantId"/>,
    /// is finalized, and is linked (as its member or a related member) to each of
    /// <paramref name="memberIds"/>.
    /// </summary>
    Task<ProofOfAuthorityDocumentCheck> CheckAsync(
        string tenantId, string documentId, IReadOnlyCollection<string> memberIds, CancellationToken ct = default);
}

/// <summary>
/// Calls <c>GET api/v1/member-documents/{id}</c>. The request names the tenant in
/// <c>X-Tenant-ID</c>; the shared outbound handler forwards the caller's own
/// token (and re-states its tenant), so member-document-service scopes the
/// lookup to the token's tenant and applies its own <c>members:read</c> check.
/// Anything other than 200 or 404 is <see cref="ProofOfAuthorityDocumentStatus.Unavailable"/>:
/// the activation is refused with 503, never accepted.
/// </summary>
public sealed class MemberDocumentProofOfAuthorityDocuments : IProofOfAuthorityDocuments
{
    public const string HttpClientName = "MemberDocumentService";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MemberDocumentProofOfAuthorityDocuments> _logger;

    public MemberDocumentProofOfAuthorityDocuments(
        IHttpClientFactory httpClientFactory, ILogger<MemberDocumentProofOfAuthorityDocuments> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ProofOfAuthorityDocumentCheck> CheckAsync(
        string tenantId, string documentId, IReadOnlyCollection<string> memberIds, CancellationToken ct = default)
    {
        string json;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"api/v1/member-documents/{Uri.EscapeDataString(documentId)}");
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(ProofOfAuthorityDocumentStatus.NotFound);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning(
                    "member-document-service returned {Status} for proof-of-authority document {DocumentId} in tenant {TenantId}",
                    (int)response.StatusCode, LogSanitizer.SafeForLog(documentId), LogSanitizer.SafeForLog(tenantId));
                return new(ProofOfAuthorityDocumentStatus.Unavailable);
            }

            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Could not reach member-document-service for proof-of-authority document {DocumentId} in tenant {TenantId}",
                LogSanitizer.SafeForLog(documentId), LogSanitizer.SafeForLog(tenantId));
            return new(ProofOfAuthorityDocumentStatus.Unavailable);
        }

        try
        {
            return Evaluate(json, tenantId, documentId, memberIds);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Unreadable member-document-service response for proof-of-authority document {DocumentId}",
                LogSanitizer.SafeForLog(documentId));
            return new(ProofOfAuthorityDocumentStatus.Unavailable);
        }
    }

    /// <summary>Applies the tenant, id, finalization and member-link rules to a document body.</summary>
    public static ProofOfAuthorityDocumentCheck Evaluate(
        string documentJson, string tenantId, string documentId, IReadOnlyCollection<string> memberIds)
    {
        using var doc = JsonDocument.Parse(documentJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("member document is not a JSON object");

        // member-document-service already scopes the lookup to the token tenant;
        // a body naming another tenant (or none) is refused all the same.
        if (!string.Equals(GetString(root, "tenantId"), tenantId, StringComparison.Ordinal))
            return new(ProofOfAuthorityDocumentStatus.WrongTenant);

        if (!string.Equals(GetString(root, "id"), documentId, StringComparison.Ordinal))
            return new(ProofOfAuthorityDocumentStatus.NotFound);

        if (!string.IsNullOrEmpty(GetString(root, "pendingUploadBlobPath")))
            return new(ProofOfAuthorityDocumentStatus.NotFinalized);

        var linked = new HashSet<string>(StringComparer.Ordinal);
        if (GetString(root, "memberId") is { Length: > 0 } owner)
            linked.Add(owner);
        if (TryGet(root, "relatedMemberIds", out var related) && related.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in related.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                    linked.Add(id);
        }

        var unlinked = memberIds.FirstOrDefault(m => !linked.Contains(m));
        return unlinked is null
            ? new(ProofOfAuthorityDocumentStatus.Verified)
            : new(ProofOfAuthorityDocumentStatus.NotLinkedToMember, unlinked);
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGet(obj, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
