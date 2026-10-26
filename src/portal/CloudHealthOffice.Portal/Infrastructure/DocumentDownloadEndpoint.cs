using System.Net;
using System.Text.RegularExpressions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// <c>GET /documents/{id}/download?subjectKind=&amp;subjectId=</c>: the link the
/// portal gives the browser for a catalog document. The browser cannot reach
/// document-service and holds no CHO token, so the portal fetches the content
/// for the signed-in user (cookie authentication) through
/// <see cref="IDocumentCatalogService"/>, whose HttpClient attaches that
/// user's CHO token. The catalog then enforces <c>documents:read</c>, the
/// subject's own read permission, the link from the document to that subject
/// and any sensitivity rule, and writes the access audit in the user's name.
/// Its 401/403/404/409 are passed on.
///
/// <para>The subject travels in the query because the catalog refuses to serve
/// a document that is not asked for through a subject — the same rule the
/// panel is built on. A document the caller cannot reach through the named
/// subject answers 404, so ids reveal nothing.</para>
///
/// <para>The response is always an attachment, with
/// <c>X-Content-Type-Options: nosniff</c> and <c>Cache-Control: no-store</c>.
/// It is a GET with no side effects, so it needs no antiforgery token.
/// Document content is never logged.</para>
/// </summary>
public static class DocumentDownloadEndpoint
{
    public const string RoutePrefix = "/documents";
    public const string Route = RoutePrefix + "/{id}/download";

    // The catalog issues GUIDs; anything that is not one plain path segment of
    // letters, digits, '-' and '_' is refused before any call.
    private static readonly Regex IdFormat = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Matches the catalog's own subject-id rule (SubjectAccess.SubjectId).
    private static readonly Regex SubjectIdFormat = new(
        "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SafeFileName = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/tiff"] = ".tiff",
    };

    /// <summary>The portal-relative link for a document, opened from one subject.</summary>
    public static string PathFor(string documentId, DocumentSubjectKind kind, string subjectId)
        => $"{RoutePrefix}/{Uri.EscapeDataString(documentId)}/download" +
           $"?subjectKind={kind}&subjectId={Uri.EscapeDataString(subjectId)}";

    public static bool IsValidId(string? documentId)
        => !string.IsNullOrEmpty(documentId) && IdFormat.IsMatch(documentId);

    public static bool IsValidSubjectId(string? subjectId)
        => !string.IsNullOrEmpty(subjectId) && SubjectIdFormat.IsMatch(subjectId);

    public static RouteHandlerBuilder MapDocumentDownload(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RoutePrefix + "/{id}/preview", (string id, string? subjectKind, string? subjectId,
            HttpContext http, IDocumentCatalogService documents, ILoggerFactory loggerFactory, CancellationToken ct) =>
                HandleCoreAsync(id, subjectKind, subjectId, http, documents, loggerFactory, ct, true))
            .RequireAuthorization().WithName("DocumentPreview");
        return endpoints.MapGet(Route, HandleAsync).RequireAuthorization().WithName("DocumentDownload");
    }

    internal static async Task<IResult> HandleAsync(
        string id,
        string? subjectKind,
        string? subjectId,
        HttpContext http,
        IDocumentCatalogService documents,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        await HandleCoreAsync(id, subjectKind, subjectId, http, documents, loggerFactory, cancellationToken, false);

    internal static async Task<IResult> HandleCoreAsync(string id, string? subjectKind, string? subjectId,
        HttpContext http, IDocumentCatalogService documents, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken, bool preview)
    {
        var logger = loggerFactory.CreateLogger(typeof(DocumentDownloadEndpoint).FullName!);
        var response = http.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        if (!IsValidId(id))
        {
            logger.LogWarning("Document download refused: malformed document id");
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<DocumentSubjectKind>(subjectKind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind)
            || !IsValidSubjectId(subjectId))
        {
            logger.LogWarning("Document {DocumentId} download refused: the subject it is opened from is missing or malformed", id);
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        HttpResponseMessage content;
        try
        {
            content = await documents.DownloadAsync(id, kind, subjectId!, cancellationToken);
        }
        catch (ChoTokenUnavailableException ex)
        {
            var status = ex.Status switch
            {
                ChoTokenStatus.NoAccess => StatusCodes.Status403Forbidden,
                ChoTokenStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status401Unauthorized,
            };
            logger.LogWarning("Document {DocumentId} download: no CHO token ({TokenStatus}); answering {Status}",
                id, ex.Status, status);
            return Results.StatusCode(status);
        }
        catch (ServiceUnavailableException)
        {
            logger.LogWarning("Document {DocumentId} download: document-service unavailable", id);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!content.IsSuccessStatusCode)
        {
            var backend = content.StatusCode;
            content.Dispose();
            var status = backend switch
            {
                HttpStatusCode.Unauthorized => StatusCodes.Status401Unauthorized,
                HttpStatusCode.Forbidden => StatusCodes.Status403Forbidden,
                HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
                HttpStatusCode.Conflict => StatusCodes.Status409Conflict, // unscanned or not finalized
                _ => StatusCodes.Status502BadGateway,
            };
            logger.LogWarning("Document {DocumentId} download refused by document-service ({BackendStatus}); answering {Status}",
                id, (int)backend, status);
            return Results.StatusCode(status);
        }

        http.Response.RegisterForDispose(content);

        // The catalog stores more types than the portal renders, and nothing
        // here is rendered: an unknown type goes out as opaque bytes, and
        // every type goes out as an attachment.
        var contentType = content.Content.Headers.ContentType?.MediaType is { } type
                          && MemberDocumentContentTypes.Allowed.Contains(type)
            ? type
            : "application/octet-stream";
        var suggested = content.Content.Headers.ContentDisposition?.FileNameStar
                        ?? content.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        var fileName = suggested is { } name && SafeFileName.IsMatch(name)
            ? name
            : id + (Extensions.TryGetValue(contentType, out var ext) ? ext : string.Empty);

        if (preview)
        {
            if (contentType is not ("application/pdf" or "image/png" or "image/jpeg"))
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'; frame-ancestors 'self'";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers.ContentDisposition = "inline";
            return Results.Stream(await content.Content.ReadAsStreamAsync(cancellationToken), contentType);
        }

        logger.LogInformation("Document {DocumentId} downloaded from {SubjectKind} ({ContentType})", id, kind, contentType);
        return Results.Stream(await content.Content.ReadAsStreamAsync(cancellationToken), contentType, fileDownloadName: fileName);
    }
}
