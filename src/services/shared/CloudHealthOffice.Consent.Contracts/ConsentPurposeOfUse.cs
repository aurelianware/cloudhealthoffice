namespace CloudHealthOffice.Consent.Contracts;

/// <summary>
/// What a consent authorizes the plan to DO with the member's data — the
/// purpose-of-use axis, orthogonal to the regulatory instrument the consent is
/// (<c>ConsentService.Models.ConsentType</c>: a §164.506 TPO disclosure, a
/// §164.508 authorization, or a sensitive-category authorization).
///
/// The two axes are deliberately separate, for the reason this type exists at
/// all: a member who authorizes one data flow has not thereby authorized every
/// other. Provider Access consent does not permit a Payer-to-Payer disclosure,
/// and vice versa. Folding purpose into the instrument type would make that
/// distinction a naming convention; keeping it a field makes it structural, and
/// it matches FHIR <c>Consent.provision.purpose</c> / HL7 v3 PurposeOfUse.
///
/// <see cref="Unspecified"/> is the default and authorizes NOTHING that requires
/// an explicit purpose. Consents written before this axis existed deserialize to
/// it, so no historical record silently becomes Payer-to-Payer authorization.
/// </summary>
public enum ConsentPurposeOfUse
{
    /// <summary>
    /// No purpose recorded. Never satisfies a purpose-specific authorization
    /// check — the migration position for records predating this axis.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// CMS-0057-F Payer-to-Payer exchange: the plan may disclose the member's
    /// data to, and request it from, another payer. Required for both
    /// directions of the exchange, including the identity disclosure a
    /// <c>$member-match</c> makes.
    /// </summary>
    PayerToPayerExchange = 1,

    /// <summary>
    /// CMS-0057-F Provider Access: the plan may disclose the member's data to
    /// an attributed provider. Recorded on the same registry, deliberately
    /// distinct from <see cref="PayerToPayerExchange"/>.
    /// </summary>
    ProviderAccess = 2,
}

/// <summary>
/// Whether a consent record permits or refuses its purpose, as FHIR
/// <c>Consent.provision.type</c> does. <see cref="Permit"/> is the default, so
/// every record written before this axis existed keeps its meaning.
///
/// A <see cref="Deny"/> record is a member's opt-out: CMS-0057-F Provider
/// Access shares by default and stops when the member opts out, so the opt-out
/// is a record of its own, in force from its effective date until it is revoked
/// or lapses.
/// </summary>
public enum ConsentProvision
{
    Permit = 0,
    Deny = 1,
}
