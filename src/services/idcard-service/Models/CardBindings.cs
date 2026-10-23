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

    /// <summary>
    /// The sponsor policy within the group, where the member is enrolled under
    /// one (plan-configuration.md §9b, §12.12). Null for a member enrolled by
    /// group number, and on every card issued before enrolment recorded
    /// policies — so a card shows the line only where there is one, rather than
    /// an empty field.
    ///
    /// Not the *other* carrier's policy number: this service does not render
    /// coordination-of-benefits detail at all.
    /// </summary>
    public string? SponsorPolicyNumber { get; set; }

    public string? SponsorName { get; set; }
    public string? SponsorSupportPhone { get; set; }

    /// <summary>
    /// The legal entity that issued the coverage, from the tenant's carrier
    /// (plan-configuration.md §12.4). Null where the carrier states no
    /// letterhead name, in which case the card is headed by the sponsor as it
    /// was before carriers existed.
    /// </summary>
    public string? IssuerName { get; set; }

    /// <summary>The issuer's address, one line, for the back of the card. Null where the carrier states none.</summary>
    public string? IssuerAddress { get; set; }

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
