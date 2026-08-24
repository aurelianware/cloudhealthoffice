namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Attachment size, MIME, and storage limits. Bound from
/// <c>HealthcareTransactions:ClaimAttachments</c>.
/// Stedi recommends limiting JSON/S3 uploads to 64MB each.
/// </summary>
public sealed class ClaimAttachmentOptions
{
    public const long StediRecommendedMaxBytes = 64L * 1024 * 1024;

    public static readonly string[] StediContentTypes =
    {
        "application/pdf",
        "image/tiff",
        "image/jpeg",
        "image/jpg",
        "image/png"
    };

    public long MaxContentLengthBytes { get; set; } = StediRecommendedMaxBytes;

    public long StediMaxContentLengthBytes { get; set; } = StediRecommendedMaxBytes;

    public string ContentContainer { get; set; } = "claim-attachments";

    public string[] AllowedContentTypes { get; set; } = StediContentTypes;

    public long EffectiveMaxBytes()
    {
        var cho = MaxContentLengthBytes > 0 ? MaxContentLengthBytes : StediRecommendedMaxBytes;
        var stedi = StediMaxContentLengthBytes > 0 ? StediMaxContentLengthBytes : StediRecommendedMaxBytes;
        return Math.Min(cho, stedi);
    }
}
