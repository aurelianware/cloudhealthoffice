using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;

namespace DocumentService.Services;

/// <summary>
/// Who may see a document (docs/architecture/document-store.md §5). Access is
/// decided by permission plus context, never by tenant alone:
/// <list type="number">
/// <item>The caller holds documents:read (enforced by the endpoint) and the
/// read permission of the subject it works from: a claim's documents need
/// claims:read.</item>
/// <item>The document is linked to that subject. A document is always opened
/// "for claim X", never just "document Y".</item>
/// <item>A sensitivity tag needs documents:read-sensitive. 42 CFR Part 2
/// records also need a consent check through consent-service, which does not
/// exist yet, so they are withheld from everyone (fail closed).</item>
/// </list>
/// </summary>
public static class SubjectAccess
{
    private static readonly Regex SubjectId = new("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.Compiled);

    /// <summary>
    /// The permission each subject's documents need. A kind whose owning
    /// screen does not exist yet (Call) maps to a permission no role holds,
    /// so its documents are closed until that service defines access.
    /// </summary>
    private static readonly IReadOnlyDictionary<DocumentLinkKind, string> ReadPermission =
        new Dictionary<DocumentLinkKind, string>
        {
            [DocumentLinkKind.Member] = "members:read",
            [DocumentLinkKind.Claim] = "claims:read",
            [DocumentLinkKind.ClaimLine] = "claims:read",
            [DocumentLinkKind.Authorization] = "authorizations:read",
            [DocumentLinkKind.Appeal] = "appeals:read",
            [DocumentLinkKind.Grievance] = "appeals:read",
            [DocumentLinkKind.Call] = "service:read",
            [DocumentLinkKind.Provider] = "providers:read",
            [DocumentLinkKind.RfaiRequest] = "rfai:read",
            [DocumentLinkKind.PaymentRun] = "payments:read",
            [DocumentLinkKind.ImportRun] = "claims:import",
            [DocumentLinkKind.StateReport] = "reports:compliance",
        };

    public static string ReadPermissionFor(DocumentLinkKind kind) => ReadPermission[kind];

    public static bool CanReadSubject(ICurrentActor actor, DocumentLinkKind kind) =>
        actor.HasPermission(ReadPermission[kind]);

    /// <summary>Parses "Kind:Id" (e.g. "Claim:CLM-1"). The id is one token; no paths, spaces or control characters.</summary>
    public static bool TryParseLink(string? value, out DocumentLink link)
    {
        link = new DocumentLink();
        if (string.IsNullOrWhiteSpace(value)) return false;
        var colon = value.IndexOf(':');
        if (colon <= 0) return false;
        return TryCreate(value[..colon], value[(colon + 1)..], out link);
    }

    public static bool TryCreate(string? kind, string? id, out DocumentLink link)
    {
        link = new DocumentLink();
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id)) return false;
        if (!Enum.TryParse<DocumentLinkKind>(kind.Trim(), ignoreCase: true, out var parsed)
            || !Enum.IsDefined(parsed)
            || kind.Trim().All(char.IsDigit))
            return false;
        var trimmed = id.Trim();
        if (!SubjectId.IsMatch(trimmed)) return false;
        link = new DocumentLink { Kind = parsed, Id = trimmed };
        return true;
    }

    /// <summary>Whether this caller may see this document's content and metadata, working from <paramref name="subject"/>.</summary>
    public static AccessDecision Decide(ICurrentActor actor, CatalogDocument document, DocumentLink subject)
    {
        if (!CanReadSubject(actor, subject.Kind))
            return AccessDecision.Deny($"needs {ReadPermission[subject.Kind]} for {subject.Kind} documents");

        if (!document.Links.Contains(subject))
            return AccessDecision.Deny($"not linked to {subject}");

        return DecideSensitivity(actor, document);
    }

    /// <summary>The sensitivity rule alone (the subject check already passed).</summary>
    public static AccessDecision DecideSensitivity(ICurrentActor actor, CatalogDocument document)
    {
        if (document.Sensitivity.Count == 0)
            return AccessDecision.Allow;

        if (document.Sensitivity.Contains(SensitivityTags.Part2Sud))
            return AccessDecision.Deny("42 CFR Part 2 record: withheld until the consent check is available");

        return actor.HasPermission(ChoRolePermissions.DocumentsReadSensitive)
            ? AccessDecision.Allow
            : AccessDecision.Deny($"sensitive ({string.Join(",", document.Sensitivity)}): needs {ChoRolePermissions.DocumentsReadSensitive}");
    }

    /// <summary>The document as this caller may see it: links to subjects it can't read are counted, not shown.</summary>
    public static DocumentView Project(ICurrentActor actor, CatalogDocument d)
    {
        var visible = d.Links.Where(l => CanReadSubject(actor, l.Kind)).ToList();
        return new DocumentView
        {
            Id = d.Id,
            Type = d.Type,
            Title = d.Title,
            ContentType = d.ContentType,
            SizeBytes = d.SizeBytes,
            Sha256 = d.Sha256,
            DocumentVersion = d.DocumentVersion,
            SupersedesId = d.SupersedesId,
            Source = d.Source,
            Links = visible,
            OtherLinks = d.Links.Count - visible.Count,
            Sensitivity = d.Sensitivity,
            RetentionPolicyId = d.Retention.PolicyId,
            RetainUntil = d.Retention.RetainUntil,
            LegalHold = d.Retention.LegalHold.Set,
            ScanStatus = d.Scan.Status,
            UploadPending = d.PendingUploadPath != null,
            ReceivedAt = d.ReceivedAt,
            CreatedAt = d.CreatedAt,
            CreatedBy = d.CreatedBy,
            CreatedOnBehalfOf = d.CreatedOnBehalfOf
        };
    }
}

public readonly record struct AccessDecision(bool Allowed, string? Reason)
{
    public static AccessDecision Allow => new(true, null);
    public static AccessDecision Deny(string reason) => new(false, reason);
}
