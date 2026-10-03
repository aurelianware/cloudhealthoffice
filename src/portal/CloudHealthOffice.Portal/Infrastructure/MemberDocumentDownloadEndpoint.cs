using System.Net;
using System.Text.RegularExpressions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Infrastructure;

/// <summary>
/// <c>GET /member-documents/{id}/download</c>: the link the portal gives the
/// browser for a member document. The browser cannot reach member-document-service
/// and holds no CHO token, so the portal fetches the document for the signed-in
/// user (cookie authentication) through <see cref="IMemberDocumentService"/>, whose
/// HttpClient attaches that user's CHO token. member-document-service then enforces
/// <c>members:read</c> and the token's tenant; its 401/403/404/409 are passed on.
///
/// The response is always an attachment, with <c>X-Content-Type-Options: nosniff</c>
/// and <c>Cache-Control: no-store</c>. It is a GET with no side effects, so it needs
/// no antiforgery token. Document content is never logged.
/// </summary>
public static class MemberDocumentDownloadEndpoint
{
    public const string RoutePrefix = "/member-documents";
    public const string Route = RoutePrefix + "/{id}/download";

    // member-document-service issues GUIDs; anything that is not one plain path
    // segment of letters, digits, '-' and '_' is refused before any call.
    private static readonly Regex IdFormat = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SafeFileName = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = ".pdf",
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/tiff"] = ".tiff",
    };

    /// <summary>The portal-relative link for a document.</summary>
    public static string PathFor(string documentId)
        => $"{RoutePrefix}/{Uri.EscapeDataString(documentId)}/download";

    public static bool IsValidId(string? documentId)
        => !string.IsNullOrEmpty(documentId) && IdFormat.IsMatch(documentId);

    public static RouteHandlerBuilder MapMemberDocumentDownload(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(Route, HandleAsync)
            .RequireAuthorization()
            .WithName("MemberDocumentDownload");

    internal static async Task<IResult> HandleAsync(
        string id,
        HttpContext http,
        IMemberDocumentService documents,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(MemberDocumentDownloadEndpoint).FullName!);
        var response = http.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";

        if (!IsValidId(id))
        {
            logger.LogWarning("Member document download refused: malformed document id");
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        MemberDocumentContent content;
        try
        {
            content = await documents.OpenDocumentContentAsync(id, cancellationToken);
        }
        catch (ChoTokenUnavailableException ex)
        {
            var status = ex.Status switch
            {
                ChoTokenStatus.NoAccess => StatusCodes.Status403Forbidden,
                ChoTokenStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status401Unauthorized,
            };
            logger.LogWarning("Member document {DocumentId} download: no CHO token ({TokenStatus}); answering {Status}",
                id, ex.Status, status);
            return Results.StatusCode(status);
        }
        catch (ServiceUnavailableException)
        {
            logger.LogWarning("Member document {DocumentId} download: member-document-service unavailable", id);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!content.Succeeded)
        {
            content.Dispose();
            var status = content.StatusCode switch
            {
                HttpStatusCode.Unauthorized => StatusCodes.Status401Unauthorized,
                HttpStatusCode.Forbidden => StatusCodes.Status403Forbidden,
                HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
                HttpStatusCode.Conflict => StatusCodes.Status409Conflict, // upload not finalized
                _ => StatusCodes.Status502BadGateway,
            };
            logger.LogWarning("Member document {DocumentId} download refused by member-document-service ({BackendStatus}); answering {Status}",
                id, (int)content.StatusCode, status);
            return Results.StatusCode(status);
        }

        http.Response.RegisterForDispose(content);

        // The service only stores allow-listed types; anything else (older records)
        // goes out as opaque bytes. Either way it is an attachment, never rendered.
        var contentType = content.ContentType is { } type && MemberDocumentContentTypes.Allowed.Contains(type)
            ? type
            : "application/octet-stream";
        var fileName = content.FileName is { } name && SafeFileName.IsMatch(name)
            ? name
            : id + (Extensions.TryGetValue(contentType, out var ext) ? ext : string.Empty);

        logger.LogInformation("Member document {DocumentId} downloaded ({ContentType})", id, contentType);
        return Results.Stream(content.Content!, contentType, fileDownloadName: fileName);
    }
}
