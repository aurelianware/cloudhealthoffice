using CloudHealthOffice.TradingPartnerService.Models;

namespace CloudHealthOffice.TradingPartnerService.Services;

/// <summary>
/// Resolves path templates from Trading Partner configuration
/// Replaces variables like {tenantId}, {transactionType}, {yyyy}, etc.
/// </summary>
public class PathResolver
{
    public string ResolveSftpPath(TradingPartner partner, string direction, string transactionType)
    {
        if (partner.SftpConfig?.Paths == null)
            throw new InvalidOperationException("SFTP paths not configured");

        var paths = direction.ToLower() == "inbound" 
            ? partner.SftpConfig.Paths.Inbound 
            : partner.SftpConfig.Paths.Outbound;

        if (!paths.TryGetValue(transactionType, out var path))
            throw new KeyNotFoundException($"Path not found for {direction}/{transactionType}");

        return path;
    }

    public string ResolveBlobPath(TradingPartner partner, string stage, string transactionType, DateTime? timestamp = null)
    {
        if (partner.BlobConfig?.Paths == null)
            throw new InvalidOperationException("Blob paths not configured");

        if (!partner.BlobConfig.Paths.TryGetValue(stage, out var template))
            throw new KeyNotFoundException($"Blob path not found for stage: {stage}");

        var now = timestamp ?? DateTime.UtcNow;

        return template
            .Replace("{tenantId}", partner.TenantId)
            .Replace("{tradingPartnerId}", partner.TradingPartnerId)
            .Replace("{environment}", partner.Environment)
            .Replace("{transactionType}", transactionType)
            .Replace("{yyyy}", now.ToString("yyyy"))
            .Replace("{MM}", now.ToString("MM"))
            .Replace("{dd}", now.ToString("dd"))
            .Replace("{HH}", now.ToString("HH"))
            .Replace("{mm}", now.ToString("mm"));
    }

    public string GetBlobContainer(TradingPartner partner)
    {
        return partner.BlobConfig?.ContainerName 
            ?? $"cho-{partner.Environment}";
    }

    public int GetRetentionDays(TradingPartner partner, string stage)
    {
        if (partner.BlobConfig?.RetentionPolicies == null)
            return 90; // Default

        return partner.BlobConfig.RetentionPolicies.TryGetValue(stage, out var days) 
            ? days 
            : 90;
    }

    public string GetX12SenderId(TradingPartner partner)
    {
        return partner.X12Config?.SenderId 
            ?? throw new InvalidOperationException("X12 Sender ID not configured");
    }

    public string GetX12ReceiverId(TradingPartner partner)
    {
        return partner.X12Config?.ReceiverId 
            ?? throw new InvalidOperationException("X12 Receiver ID not configured");
    }

    /// <summary>
    /// Generates full SFTP path for file upload/download
    /// Example: /bcbs-florida/availity/prod/outbound/277/response-20260207.edi
    /// </summary>
    public string BuildSftpFilePath(TradingPartner partner, string direction, string transactionType, string fileName)
    {
        var basePath = ResolveSftpPath(partner, direction, transactionType);
        return $"{basePath}/{fileName}";
    }

    /// <summary>
    /// Generates full blob path for file storage
    /// Example: prod/bcbs-florida/availity/raw/275/2026/02/07/attachment-001.edi
    /// </summary>
    public string BuildBlobFilePath(TradingPartner partner, string stage, string transactionType, string fileName, DateTime? timestamp = null)
    {
        var basePath = ResolveBlobPath(partner, stage, transactionType, timestamp);
        return $"{basePath}/{fileName}";
    }
}
