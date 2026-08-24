using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Vendor-neutral attachment validation: MIME, size, file names, claim
/// association, service-line matching, and content-scan gates.
/// </summary>
public static class ClaimAttachmentRules
{
    public static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "attachment.bin";
        }

        var name = Path.GetFileName(fileName.Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return "attachment.bin";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
            }
        }

        var sanitized = builder.ToString().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "attachment.bin";
        }

        return sanitized.Length <= 128 ? sanitized : sanitized[..128];
    }

    public static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var value = contentType.Trim();
        var semi = value.IndexOf(';');
        if (semi >= 0)
        {
            value = value[..semi];
        }

        return value.Trim().ToLowerInvariant();
    }

    public static bool IsSupportedContentType(string? contentType, ClaimAttachmentOptions options)
    {
        var normalized = NormalizeContentType(contentType);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var allowed = options.AllowedContentTypes is { Length: > 0 }
            ? options.AllowedContentTypes
            : ClaimAttachmentOptions.StediContentTypes;
        return allowed.Any(t => string.Equals(NormalizeContentType(t), normalized, StringComparison.Ordinal));
    }

    public static string ExtensionForContentType(string contentType) =>
        NormalizeContentType(contentType) switch
        {
            "application/pdf" => ".pdf",
            "image/tiff" => ".tiff",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            _ => ".bin"
        };

    public static string StorageKey(
        string tenantId,
        string transmissionId,
        string attachmentId,
        string checksumSha256,
        string contentType)
    {
        var ext = ExtensionForContentType(contentType);
        return $"{SanitizePathSegment(tenantId)}/{SanitizePathSegment(transmissionId)}/{SanitizePathSegment(attachmentId)}/{checksumSha256}{ext}";
    }

    public static string SanitizePathSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString();
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }

    public static async Task<string> ComputeSha256HexAsync(Stream content, CancellationToken ct)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var hash = await SHA256.HashDataAsync(content, ct).ConfigureAwait(false);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ChecksumPrefix(string? checksum) =>
        string.IsNullOrEmpty(checksum)
            ? string.Empty
            : checksum.Length <= 8 ? checksum : checksum[..8];

    /// <summary>
    /// Strip control characters (including CR/LF/Unicode separators) so
    /// caller-supplied ids cannot forge additional log lines.
    /// </summary>
    public static string? SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var sanitized = builder.ToString();
        return sanitized.Length <= 128 ? sanitized : sanitized[..128];
    }

    public static (GatewayErrorCategory Category, string Message)? ValidateRequest(
        ClaimAttachmentSubmissionRequest request,
        ClaimAttachmentOptions options)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimId) ||
            string.IsNullOrWhiteSpace(request.TransmissionId) ||
            string.IsNullOrWhiteSpace(request.AttachmentId))
        {
            return (GatewayErrorCategory.Validation,
                "ClaimId, TransmissionId, and AttachmentId are required.");
        }

        if (request.AttachmentVersion < 1)
        {
            return (GatewayErrorCategory.Validation, "AttachmentVersion must be at least 1.");
        }

        if (request.Mode == ClaimAttachmentMode.Solicited)
        {
            return (GatewayErrorCategory.NotSupported,
                "Solicited 275 attachments are not supported. Stedi APIs and SFTP accept unsolicited attachments only.");
        }

        var contentType = NormalizeContentType(request.ContentType);
        if (string.IsNullOrEmpty(contentType) && request.Content is not null)
        {
            contentType = NormalizeContentType(request.Content.ContentType);
        }

        if (!IsSupportedContentType(contentType, options))
        {
            return (GatewayErrorCategory.UnsupportedContentType,
                "Attachment content type is not supported.");
        }

        var length = request.ContentLength > 0 ? request.ContentLength : request.Content?.ContentLength ?? 0;
        if (length <= 0)
        {
            return (GatewayErrorCategory.Validation, "Attachment content length must be greater than zero.");
        }

        if (length > options.EffectiveMaxBytes())
        {
            return (GatewayErrorCategory.AttachmentTooLarge,
                "Attachment exceeds the configured maximum size.");
        }

        if (request.Content is null ||
            string.IsNullOrWhiteSpace(request.Content.StorageKey) ||
            string.IsNullOrWhiteSpace(request.Content.ChecksumSha256))
        {
            return (GatewayErrorCategory.AttachmentNotFound,
                "A secure content reference is required. Attachment bytes are not accepted inline.");
        }

        var scan = request.Content.ScanStatus;
        if (scan is ClaimAttachmentScanStatus.Quarantined
            or ClaimAttachmentScanStatus.Unsafe
            or ClaimAttachmentScanStatus.ScanFailed)
        {
            return (GatewayErrorCategory.AttachmentUnsafe,
                "Attachment content did not pass content-safety screening.");
        }

        return null;
    }

    public static (GatewayErrorCategory Category, string Message)? ValidateAssociation(
        ClaimAttachmentSubmissionRequest request,
        ClaimTransmissionRecord transmission)
    {
        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !string.Equals(request.TenantId, transmission.TenantId, StringComparison.Ordinal))
        {
            return (GatewayErrorCategory.ClaimMismatch, "Tenant does not match the claim transmission.");
        }

        if (!string.Equals(request.ClaimId, transmission.ClaimId, StringComparison.Ordinal))
        {
            return (GatewayErrorCategory.ClaimMismatch, "ClaimId does not match the claim transmission.");
        }

        if (!string.IsNullOrWhiteSpace(request.PayerId) &&
            !string.IsNullOrWhiteSpace(transmission.PayerId) &&
            !string.Equals(request.PayerId, transmission.PayerId, StringComparison.OrdinalIgnoreCase))
        {
            return (GatewayErrorCategory.ClaimMismatch, "PayerId does not match the claim transmission.");
        }

        if (request.ServiceLineNumber.HasValue)
        {
            if (transmission.ClaimType == GatewayClaimType.Dental)
            {
                return (GatewayErrorCategory.NotSupported,
                    "Service-line attachments are not supported for dental claims on this gateway.");
            }

            if (transmission.ServiceLineNumbers.Count == 0 ||
                !transmission.ServiceLineNumbers.Contains(request.ServiceLineNumber.Value))
            {
                return (GatewayErrorCategory.ServiceLineNotFound,
                    "Service line was not present on the original submitted claim.");
            }
        }

        return null;
    }
}
