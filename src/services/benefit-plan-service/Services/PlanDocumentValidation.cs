using BenefitPlanService.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Producer-boundary validation for <see cref="PlanDocumentReference"/>.
///
/// Deliberately NOT wired into the property setter: setter validation would
/// break Mongo hydration (any historical malformed document becomes
/// unreadable) and would turn JSON deserialization failures into opaque
/// 500s instead of clean 400s with field-level detail. Validate here at
/// every trust boundary — controller input, external imports, seeders — and
/// leave the model itself trusting.
/// </summary>
public static class PlanDocumentValidation
{
    /// <summary>
    /// Validate a document hash. The hash MUST be a Base64-encoded SHA-256
    /// digest — exactly 32 decoded bytes — to match FHIR
    /// <c>DocumentReference.content.attachment.hash</c>.
    ///
    /// Null or empty input is accepted (the field is optional). On any
    /// other invalid input this throws <see cref="ArgumentException"/> with
    /// <paramref name="fieldName"/> identifying the offending field so the
    /// caller can surface a clean validation error.
    /// </summary>
    public static void ValidateHash(string? hash, string fieldName)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return;
        }

        Span<byte> buffer = stackalloc byte[64];
        if (!Convert.TryFromBase64String(hash, buffer, out var written))
        {
            throw new ArgumentException(
                $"{fieldName} must be Base64-encoded; got a value that is not valid Base64.",
                fieldName);
        }

        if (written != 32)
        {
            throw new ArgumentException(
                $"{fieldName} must be a Base64-encoded SHA-256 digest (32 bytes); got {written} bytes.",
                fieldName);
        }
    }

    /// <summary>
    /// The reserved internal-reference prefix that
    /// <see cref="PlanDocumentReference.Location"/> may carry once Phase 2
    /// migrates plan documents into member-document-service. Sourced from
    /// <see cref="FhirEndpointProjector.InternalReferencePrefix"/> so the
    /// validator and the projector cannot drift. Copilot review BP 5.9.
    /// </summary>
    public const string InternalReferencePrefix = FhirEndpointProjector.InternalReferencePrefix;

    /// <summary>
    /// Validate a document <c>Location</c> against
    /// <see cref="PlanDocumentLocationPolicy"/>: either the reserved
    /// internal reference <c>documentreference/{id}</c>, or an HTTPS URL
    /// whose host is in <paramref name="allowedHosts"/> (empty by default,
    /// so only the internal reference passes). <c>javascript:</c>,
    /// <c>data:</c>, <c>file:</c>, plain HTTP, userinfo, IP literals and
    /// internal / cluster hosts are always rejected.
    ///
    /// <para>
    /// Producer-boundary only — setter-side validation would break Mongo
    /// hydration for any historical malformed document (same trust posture
    /// as <see cref="ValidateHash"/>). Stored values that fail the rule are
    /// withheld on read instead.
    /// </para>
    /// </summary>
    public static void ValidateLocation(
        string? location, string fieldName, IEnumerable<string>? allowedHosts = null)
        => Policy(allowedHosts).ValidateLocation(location, fieldName);

    /// <summary>
    /// Validate every document attached to a plan. Iterates each entry and
    /// delegates to <see cref="ValidateLocation"/> and
    /// <see cref="ValidateHash"/>, labelling each field with the document
    /// index so the caller can report which document failed.
    /// </summary>
    public static void ValidateDocuments(
        IEnumerable<PlanDocumentReference>? documents, IEnumerable<string>? allowedHosts = null)
        => Policy(allowedHosts).ValidateDocuments(documents);

    private static PlanDocumentLocationPolicy Policy(IEnumerable<string>? allowedHosts)
        => allowedHosts is null ? PlanDocumentLocationPolicy.Empty : new PlanDocumentLocationPolicy(allowedHosts);
}
