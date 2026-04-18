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
    /// Validate every document attached to a plan. Iterates each entry and
    /// delegates to <see cref="ValidateHash"/>, labelling the field with
    /// the document index so the caller can report which document failed.
    /// </summary>
    public static void ValidateDocuments(IEnumerable<PlanDocumentReference>? documents)
    {
        if (documents == null) return;

        var index = 0;
        foreach (var doc in documents)
        {
            ValidateHash(doc.ContentHashSha256, $"documents[{index}].contentHashSha256");
            index++;
        }
    }
}
