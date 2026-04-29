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
    /// Validate a document <c>Location</c>. The Location must be either
    /// an HTTPS URL (the operator-authored external address Endpoint
    /// projection expects) or the reserved internal reference of the form
    /// <c>documentreference/{id}</c> (Phase 2 forward-compat).
    ///
    /// <para>
    /// Null or empty input is rejected — Location is <c>[Required]</c> on
    /// the model. HTTP (plaintext) is rejected because every regulatory
    /// surface this projection serves (ACA SBC publication, CMS
    /// Transparency in Coverage MRFs, CMS-0057-F formulary discoverability)
    /// requires HTTPS. Producer-boundary only — setter-side validation
    /// would break Mongo hydration for any historical malformed document
    /// (same trust posture as <see cref="ValidateHash"/>).
    /// </para>
    /// </summary>
    public static void ValidateLocation(string? location, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException(
                $"{fieldName} is required.",
                fieldName);
        }

        if (location.StartsWith(InternalReferencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Reserved internal-reference form. Beyond the prefix we
            // accept any non-empty body — the resolver in Phase 2 owns
            // any further shape constraints. We DO require a non-empty
            // body so "documentreference/" alone is rejected as
            // malformed.
            if (location.Length <= InternalReferencePrefix.Length)
            {
                throw new ArgumentException(
                    $"{fieldName} must include an id after '{InternalReferencePrefix}'; got '{location}'.",
                    fieldName);
            }
            return;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{fieldName} must be an HTTPS URL or '{InternalReferencePrefix}{{id}}'; got '{location}'.",
                fieldName);
        }
    }

    /// <summary>
    /// Validate every document attached to a plan. Iterates each entry and
    /// delegates to <see cref="ValidateHash"/> and
    /// <see cref="ValidateLocation"/>, labelling each field with the
    /// document index so the caller can report which document failed.
    /// </summary>
    public static void ValidateDocuments(IEnumerable<PlanDocumentReference>? documents)
    {
        if (documents == null) return;

        var index = 0;
        foreach (var doc in documents)
        {
            ValidateLocation(doc.Location, $"documents[{index}].location");
            ValidateHash(doc.ContentHashSha256, $"documents[{index}].contentHashSha256");
            index++;
        }
    }
}
