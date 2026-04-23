namespace AppealsService.Services;

/// <summary>
/// Encrypts and decrypts PHI-adjacent free-text fields on an appeal record
/// (<c>PatientName</c>, <c>AppealReason</c>, <c>DenialReason</c>,
/// <c>AppealNote.NoteText</c>, <c>AppealDecision.DecisionReason</c>,
/// <c>AppealDecision.ReviewerNotes</c>, <c>ClinicalDocument.Summary</c>,
/// <c>AppealAttachment.Description</c>). Null/empty values pass through
/// unchanged so partial records — a draft appeal created without a denial
/// reason, for example — do not force spurious ciphertext.
/// </summary>
public interface IAppealFieldEncryptor
{
    Task<string?> EncryptAsync(string? plaintext, CancellationToken ct = default);
    Task<string?> DecryptAsync(string? ciphertext, CancellationToken ct = default);
}
