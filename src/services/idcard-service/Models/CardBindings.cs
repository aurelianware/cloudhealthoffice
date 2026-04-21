namespace IdCardService.Models;

/// <summary>
/// Resolved token bindings passed to the renderer. Any field can be null when
/// the upstream service returns no value — the renderer replaces missing tokens
/// with an empty string so the card still renders.
/// </summary>
public class CardBindings
{
    public string MemberId { get; set; } = string.Empty;
    public string MemberNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public DateTime? DateOfBirth { get; set; }
    public string? Gender { get; set; }

    public string? GroupNumber { get; set; }
    public string? SponsorName { get; set; }
    public string? SponsorSupportPhone { get; set; }

    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? NetworkName { get; set; }
    public string? CoverageLevel { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    public string? PcpName { get; set; }
    public string? PcpPhone { get; set; }

    /// <summary>Formatted summary lines for common copays (e.g. "Office $20 / ER $150").</summary>
    public string? CopaySummary { get; set; }

    public string CardId { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public string? LanguageCode { get; set; }
}
