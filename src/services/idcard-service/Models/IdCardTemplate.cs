namespace IdCardService.Models;

/// <summary>
/// Visual + copy template for an ID card, resolved per (sponsor, plan) pair.
/// <see cref="LayoutSvg"/> is an SVG document containing text token
/// placeholders (e.g. <c>{{MemberName}}</c>, <c>{{PlanName}}</c>,
/// <c>{{IssuedAt}}</c>) that the renderer substitutes before rasterizing
/// to PDF/PNG. QR imagery is <em>not</em> a template token; it is drawn
/// onto the PDF/PNG canvas separately by <see cref="IdCardService.Services.IdCardGenerator"/>.
/// </summary>
public class IdCardTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Null on sponsor-default and global-default templates.</summary>
    public string? SponsorId { get; set; }

    /// <summary>Null on sponsor-default and global-default templates.</summary>
    public string? PlanId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string LayoutSvg { get; set; } = string.Empty;

    /// <summary>member-document-service blob id of the sponsor/brand logo.</summary>
    public string? LogoBlobId { get; set; }

    public string BackText { get; set; } = string.Empty;
    public List<string> Disclaimers { get; set; } = new();

    /// <summary>BCP-47 codes. When empty, treat as ["en-US"].</summary>
    public List<string> SupportedLanguages { get; set; } = new();

    public bool IsGlobalDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
