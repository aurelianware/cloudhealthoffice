using System.Security.Cryptography;
using System.Text;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// What Cloud Health Office does with each resource type arriving in a
/// Payer-to-Payer package, and how an imported resource is identified.
///
/// The supported inventory is taken from what CHO's FHIR surface ACTUALLY serves
/// (its CapabilityStatement and resource controllers) - not from the CMS wish
/// list. Claiming to ingest a Condition or an Observation while CHO has nowhere
/// to serve it from would be a false claim; those types are recorded as
/// unsupported by name, and the whole validated package is archived, instead.
/// </summary>
public static class PayerToPayerImportPolicy
{
    /// <summary>
    /// Member history CHO ingests: the financial and encounter record a
    /// Payer-to-Payer exchange exists to carry. Every type here is served by a
    /// CHO FHIR controller today.
    /// </summary>
    private static readonly HashSet<string> MemberHistoryTypes = new(StringComparer.Ordinal)
    {
        "ExplanationOfBenefit",   // CARIN EOB - ExplanationOfBenefitController
        "Claim",                  // ClaimController
        "ClaimResponse",          // ClaimResponseController
        "Encounter",              // EncounterController
        "DocumentReference",      // DocumentReferenceController
    };

    /// <summary>
    /// Administrative context stored for reference resolution and traceability
    /// ONLY. These never become CHO's authoritative records: the remote Patient
    /// does not replace CHO's member identity, and a prior payer's Coverage does
    /// not touch the member's current enrollment.
    /// </summary>
    private static readonly HashSet<string> AdministrativeTypes = new(StringComparer.Ordinal)
    {
        "Patient",
        "Coverage",
        "Organization",
        "Practitioner",
        "PractitionerRole",
        "Provenance",
    };

    public static ImportedResourceClass Classify(string? resourceType) => resourceType switch
    {
        null or "" => ImportedResourceClass.Unsupported,
        var t when MemberHistoryTypes.Contains(t) => ImportedResourceClass.MemberHistory,
        var t when AdministrativeTypes.Contains(t) => ImportedResourceClass.AdministrativeReference,
        _ => ImportedResourceClass.Unsupported,
    };

    /// <summary>The member-history types CHO ingests, for documentation and status reporting.</summary>
    public static IReadOnlyList<string> SupportedMemberHistoryTypes =>
        MemberHistoryTypes.OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>The administrative types CHO stores as reference-only context.</summary>
    public static IReadOnlyList<string> AdministrativeReferenceTypes =>
        AdministrativeTypes.OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Deterministic identity of an imported resource. The tuple is
    /// tenant + local member + source payer + resource type + source resource id,
    /// hashed so the stored key carries no readable member or clinical detail.
    ///
    /// Two properties matter, and both are tested:
    ///   * replaying the same package resolves to the same key, so an import
    ///     updates in place instead of duplicating the member's history;
    ///   * the same source resource id from a DIFFERENT payer is a DIFFERENT key,
    ///     so two payers' records are never silently merged.
    /// </summary>
    public static string ImportKey(
        string tenantId, string memberId, string sourcePayerId, string resourceType, string sourceResourceId)
    {
        // Unit separator: it cannot appear in an identifier, so two distinct
        // tuples can never collide through concatenation.
        var tuple = string.Join('\u001F', tenantId, memberId, sourcePayerId, resourceType, sourceResourceId);
        return Sha256Hex(tuple);
    }

    /// <summary>Content hash of a stored resource - distinguishes "same again" from "changed".</summary>
    public static string ContentHash(string resourceJson) => Sha256Hex(resourceJson);

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
