namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral person on an eligibility inquiry. Used for both the
/// subscriber (policyholder / insured) and the patient (person receiving
/// services). Independent of any clearinghouse transport shape.
/// </summary>
public sealed class GatewayEligibilityPerson
{
    /// <summary>Well-known <see cref="RelationshipToSubscriber"/> values.</summary>
    public static class Relationship
    {
        public const string Self = "self";
        public const string Spouse = "spouse";
        public const string Child = "child";
        public const string Other = "other";
    }

    /// <summary>Member / subscriber identifier as known to the payer, when present.</summary>
    public string? MemberId { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Administrative sex when known (<c>M</c>/<c>F</c>). Omit rather than
    /// send unknown — some 276 payers reject <c>U</c>.
    /// </summary>
    public string? Gender { get; set; }

    /// <summary>
    /// Relationship of this person to the subscriber (e.g. self, spouse, child).
    /// Optional; omit rather than invent. <see cref="Relationship.Self"/> means
    /// this person is the subscriber.
    /// </summary>
    public string? RelationshipToSubscriber { get; set; }

    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(MemberId) ||
        !string.IsNullOrWhiteSpace(FirstName) ||
        !string.IsNullOrWhiteSpace(LastName) ||
        DateOfBirth is not null;

    public bool IsSelf =>
        string.Equals(RelationshipToSubscriber, Relationship.Self, StringComparison.OrdinalIgnoreCase);
}
