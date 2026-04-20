using MemberDocumentService.Middleware;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text.Json;

namespace MemberDocumentService.Controllers;

[ApiController]
public class MemberDocumentsController : ControllerBase
{
    private const string DefaultContainer = "member-documents";

    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberDocumentRepository _repository;
    private readonly IMemberDocumentBlobService _blobService;
    private readonly IRetentionPolicyService _retentionPolicyService;

    public MemberDocumentsController(
        IMemberDocumentRepository repository,
        IMemberDocumentBlobService blobService,
        IRetentionPolicyService retentionPolicyService)
    {
        _repository = repository;
        _blobService = blobService;
        _retentionPolicyService = retentionPolicyService;
    }

    [HttpPost("api/v1/member-documents")]
    [Consumes("multipart/form-data", "application/json")]
    public async Task<IActionResult> CreateMemberDocument(
        [FromForm] CreateMemberDocumentRequest? formRequest,
        [FromForm] IFormFile? file,
        CancellationToken ct)
    {
        if (Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var presigned = JsonSerializer.Deserialize<PresignedUploadRequest>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (presigned == null)
            {
                return BadRequest("Invalid pre-signed upload request.");
            }

            return await CreatePresignedUploadAsync(presigned);
        }

        if (formRequest == null)
        {
            return BadRequest("Document metadata is required.");
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("A file is required for multipart uploads.");
        }

        var retention = _retentionPolicyService.ResolvePolicy(
            formRequest.StateCode,
            formRequest.CoverageTerminationDate,
            formRequest.RetentionPolicyId);

        var id = Guid.NewGuid().ToString();
        var blobPath = BuildBlobPath(formRequest.MemberId, id, file.FileName);

        await using var stream = file.OpenReadStream();
        var hash = await ComputeSha256Async(stream, ct);
        stream.Position = 0;

        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, formRequest.LegalHold);

        var sizeBytes = await _blobService.UploadAsync(
            DefaultContainer,
            blobPath,
            stream,
            file.ContentType,
            tags,
            ct);

        var document = new MemberDocument
        {
            Id = id,
            TenantId = TenantId,
            MemberId = formRequest.MemberId,
            Category = formRequest.Category,
            Subcategory = formRequest.Subcategory,
            Source = formRequest.Source,
            EffectiveDate = formRequest.EffectiveDate,
            ExpirationDate = formRequest.ExpirationDate,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            RelatedMemberIds = formRequest.RelatedMemberIds ?? new List<string>(),
            LinkedResources = formRequest.LinkedResources ?? new List<string>(),
            BlobContainer = DefaultContainer,
            BlobPath = blobPath,
            ContentType = file.ContentType,
            SizeBytes = sizeBytes,
            ContentHashSha256 = hash,
            UploadedBy = ResolveUploadedBy(formRequest.UploadedBy),
            UploadedDate = DateTime.UtcNow,
            LegalHold = formRequest.LegalHold,
            StateCode = formRequest.StateCode,
            CoverageTerminationDate = formRequest.CoverageTerminationDate
        };

        var created = await _repository.CreateAsync(document);
        return CreatedAtAction(nameof(GetMemberDocument), new { id = created.Id }, created);
    }

    [HttpGet("api/v1/member-documents/{id}")]
    public async Task<IActionResult> GetMemberDocument(string id)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        return Ok(doc);
    }

    [HttpGet("api/v1/member-documents/{id}/content")]
    public async Task<IActionResult> GetMemberDocumentContent(string id, CancellationToken ct)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        var stream = await _blobService.DownloadAsync(doc.BlobContainer, doc.BlobPath, ct);
        var fileName = $"{doc.Id}{Path.GetExtension(doc.BlobPath)}";
        return File(stream, doc.ContentType, fileName);
    }

    [HttpGet("api/v1/members/{memberId}/documents")]
    public async Task<IActionResult> ListMemberDocuments(string memberId, [FromQuery] string? category = null)
    {
        var documents = await _repository.ListByMemberIdAsync(TenantId, memberId, category);
        return Ok(documents);
    }

    [HttpPut("api/v1/member-documents/{id}/legal-hold")]
    public async Task<IActionResult> UpdateLegalHold(string id, [FromBody] LegalHoldRequest request, CancellationToken ct)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        doc.LegalHold = request.LegalHold;
        var updated = await _repository.UpdateAsync(doc);

        var retention = _retentionPolicyService.ResolvePolicy(doc.StateCode, doc.CoverageTerminationDate, doc.RetentionPolicyId);
        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, request.LegalHold);
        await _blobService.SetTagsAsync(doc.BlobContainer, doc.BlobPath, tags, ct);

        return Ok(updated);
    }

    /// <summary>
    /// Finalizes a pre-signed upload by applying blob lifecycle tags (retention/legalHold)
    /// and updating the DB record with the actual blob size.  Call this endpoint after the
    /// client has completed the direct-to-blob PUT using the SAS URL returned by the
    /// pre-signed upload flow.
    /// </summary>
    [HttpPost("api/v1/member-documents/{id}/finalize")]
    public async Task<IActionResult> FinalizeUpload(string id, CancellationToken ct)
    {
        var doc = await _repository.GetByIdAsync(TenantId, id);
        if (doc == null)
        {
            return NotFound();
        }

        // Apply lifecycle tags that were deferred because the blob didn't exist yet.
        var retention = _retentionPolicyService.ResolvePolicy(doc.StateCode, doc.CoverageTerminationDate, doc.RetentionPolicyId);
        var tags = BuildLifecycleTags(retention.PolicyId, retention.RetentionUntilDate, doc.LegalHold);
        await _blobService.SetTagsAsync(doc.BlobContainer, doc.BlobPath, tags, ct);

        // Sync the blob size into the metadata record.
        doc.SizeBytes = await _blobService.GetBlobSizeAsync(doc.BlobContainer, doc.BlobPath, ct);
        var updated = await _repository.UpdateAsync(doc);

        return Ok(updated);
    }

    [HttpGet("api/v1/members/{memberId}/fhir/DocumentReference")]
    public async Task<IActionResult> GetDocumentReferences(string memberId, [FromQuery] string? category = null)
    {
        var docs = await _repository.ListByMemberIdAsync(TenantId, memberId, category);

        var entries = docs.Select(d => new
        {
            resource = new
            {
                resourceType = "DocumentReference",
                id = d.Id,
                status = "current",
                type = new
                {
                    text = string.IsNullOrWhiteSpace(d.Subcategory) ? d.Category : $"{d.Category}/{d.Subcategory}"
                },
                subject = new
                {
                    reference = $"Patient/{d.MemberId}"
                },
                date = d.UploadedDate,
                content = new[]
                {
                    new
                    {
                        attachment = new
                        {
                            contentType = d.ContentType,
                            url = $"/api/v1/member-documents/{d.Id}/content",
                            title = d.Category,
                            size = d.SizeBytes,
                            // FHIR R4 Attachment.hash is base64Binary; we store SHA-256 as hex
                            // and convert here. The digest algorithm is SHA-256.
                            hash = ConvertHexHashToBase64(d.ContentHashSha256)
                        }
                    }
                },
                context = new
                {
                    related = d.LinkedResources.Select(link => new { reference = link }).ToArray()
                }
            }
        }).ToArray();

        var bundle = new
        {
            resourceType = "Bundle",
            type = "searchset",
            total = entries.Length,
            entry = entries
        };

        return Ok(bundle);
    }

    private async Task<IActionResult> CreatePresignedUploadAsync(PresignedUploadRequest request)
    {
        if (!TryValidateModel(request))
        {
            return ValidationProblem(ModelState);
        }

        var retention = _retentionPolicyService.ResolvePolicy(
            request.StateCode,
            request.CoverageTerminationDate,
            request.RetentionPolicyId);

        var documentId = Guid.NewGuid().ToString();
        var blobPath = BuildBlobPath(request.MemberId, documentId, request.FileName);
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);

        var uploadUri = _blobService.GenerateUploadSasUri(DefaultContainer, blobPath, request.ContentType, expires);
        if (uploadUri == null)
        {
            return StatusCode(StatusCodes.Status501NotImplemented,
                "Pre-signed URL flow requires account-key-backed blob credentials.");
        }

        var document = new MemberDocument
        {
            Id = documentId,
            TenantId = TenantId,
            MemberId = request.MemberId,
            Category = request.Category,
            Subcategory = request.Subcategory,
            Source = MemberDocumentSource.Uploaded,
            RetentionPolicyId = retention.PolicyId,
            RetentionUntilDate = retention.RetentionUntilDate,
            BlobContainer = DefaultContainer,
            BlobPath = blobPath,
            ContentType = request.ContentType,
            UploadedBy = ResolveUploadedBy(request.UploadedBy),
            UploadedDate = DateTime.UtcNow,
            LegalHold = request.LegalHold,
            StateCode = request.StateCode,
            CoverageTerminationDate = request.CoverageTerminationDate
        };

        await _repository.CreateAsync(document);

        return Ok(new PresignedUploadResponse
        {
            DocumentId = documentId,
            UploadUrl = uploadUri.ToString(),
            BlobPath = blobPath,
            ExpiresAtUtc = expires.UtcDateTime
        });
    }

    private static string BuildBlobPath(string memberId, string documentId, string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return $"members/{memberId}/{documentId}{ext}";
    }

    private static IDictionary<string, string> BuildLifecycleTags(string retentionPolicyId, DateTime retentionUntilDate, bool legalHold)
    {
        return new Dictionary<string, string>
        {
            ["retentionPolicyId"] = retentionPolicyId,
            ["retentionUntilDate"] = retentionUntilDate.ToString("yyyy-MM-dd"),
            ["legalHold"] = legalHold ? "true" : "false"
        };
    }

    private async Task<string> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ConvertHexHashToBase64(string? hexHash)
    {
        if (string.IsNullOrEmpty(hexHash))
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hexHash));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private string ResolveUploadedBy(string? uploadedBy)
    {
        if (!string.IsNullOrWhiteSpace(uploadedBy))
        {
            return uploadedBy;
        }

        return User?.Identity?.Name ?? "system";
    }
}
