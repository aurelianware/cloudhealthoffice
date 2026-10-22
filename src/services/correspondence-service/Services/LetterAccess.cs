using CloudHealthOffice.Infrastructure.Security;
using CorrespondenceService.Models;

namespace CorrespondenceService.Services;

/// <summary>
/// A letter is PHI about its subjects. Reading one needs
/// <c>correspondence:read</c> (the endpoint) and the read permission of at
/// least one of its subjects, the same mapping the document catalog uses: a
/// claim's letters need <c>claims:read</c>. The PDF itself is opened through
/// the catalog, which applies its own access rules and audit.
/// </summary>
public static class LetterAccess
{
    public static readonly IReadOnlyDictionary<string, string> SubjectReadPermission =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Member"] = "members:read",
            ["Claim"] = "claims:read",
            ["ClaimLine"] = "claims:read",
            ["Authorization"] = "authorizations:read",
            ["Appeal"] = "appeals:read",
            ["Grievance"] = "appeals:read",
            ["Call"] = "calls:read",
            ["Provider"] = "providers:read",
            ["RfaiRequest"] = "rfai:read",
            ["PaymentRun"] = "payments:read",
        };

    public static bool IsKnownSubjectKind(string kind) => SubjectReadPermission.ContainsKey(kind);

    /// <summary>The catalog's spelling of a kind ("member" → "Member").</summary>
    public static string CanonicalKind(string kind) =>
        SubjectReadPermission.Keys.First(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));

    public static bool CanReadSubject(ICurrentActor actor, string kind) =>
        SubjectReadPermission.TryGetValue(kind, out var permission) && actor.HasPermission(permission);

    public static bool CanRead(ICurrentActor actor, Letter letter) =>
        letter.Subjects.Any(s => CanReadSubject(actor, s.Kind));
}
