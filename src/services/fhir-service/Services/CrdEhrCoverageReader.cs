using System.Net.Http.Headers;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Models;
using FhirService.Services.Identity;
using Microsoft.Extensions.Options;

namespace FhirService.Services;

/// <summary>What CHO learned about the EHR's Coverage when the hook did not prefetch it.</summary>
public sealed record CrdEhrCoverageRead(JsonElement? Coverage, bool Failed)
{
    public static readonly CrdEhrCoverageRead NotAttempted = new(null, false);
}

public interface ICrdEhrCoverageReader
{
    /// <summary>
    /// Reads the patient's active Coverage from the hook's <c>fhirServer</c>,
    /// with the <c>fhirAuthorization</c> the EHR granted. Not attempted unless
    /// the caller is a trusted CDS Client and the server is its own.
    /// </summary>
    Task<CrdEhrCoverageRead> ReadAsync(CrdHookRequest request, CancellationToken ct);
}

/// <summary>
/// CDS Hooks lets a service read what the EHR did not prefetch from the EHR's
/// FHIR server. CHO does that for one thing only, the active Coverage, because
/// without it there is no Coverage for a coverage-information answer to name.
///
/// <para>
/// <c>fhirServer</c> is caller-supplied, so it is an SSRF vector: it is read
/// only for a caller authenticated as a configured CDS Client, and only when it
/// is on that client's own origin (the origin of its <c>iss</c>). A failed
/// read is reported as such, so the hook answers <c>indeterminate</c> for a
/// technical reason instead of pretending the coverage was absent.
/// </para>
/// </summary>
public sealed class CrdEhrCoverageReader : ICrdEhrCoverageReader
{
    public const string HttpClientName = "CrdEhrFhir";

    private readonly IHttpClientFactory _http;
    private readonly IHttpContextAccessor _accessor;
    private readonly IOptionsMonitor<CdsHooksOptions> _cds;
    private readonly ILogger<CrdEhrCoverageReader> _logger;

    public CrdEhrCoverageReader(
        IHttpClientFactory http, IHttpContextAccessor accessor, IOptionsMonitor<CdsHooksOptions> cds,
        ILogger<CrdEhrCoverageReader> logger)
    {
        _http = http;
        _accessor = accessor;
        _cds = cds;
        _logger = logger;
    }

    public async Task<CrdEhrCoverageRead> ReadAsync(CrdHookRequest request, CancellationToken ct)
    {
        var user = _accessor.HttpContext?.User;
        var patientId = request.Context?.PatientId;
        if (!FhirCallerSchemes.IsCdsHooksClient(user)
            || string.IsNullOrWhiteSpace(patientId)
            || !Uri.TryCreate(request.FhirServer, UriKind.Absolute, out var server))
        {
            return CrdEhrCoverageRead.NotAttempted;
        }

        var client = _cds.CurrentValue.ClientFor(user!.FindFirst("iss")?.Value);
        if (client is null
            || !Uri.TryCreate(client.Issuer, UriKind.Absolute, out var issuer)
            || !string.Equals(server.GetLeftPart(UriPartial.Authority), issuer.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("CRD will not read fhirServer {Server}: it is not the calling CDS Client's own server",
                LogSanitizer.Sanitize(server.GetLeftPart(UriPartial.Authority)));
            return CrdEhrCoverageRead.NotAttempted;
        }

        var url = $"{server.ToString().TrimEnd('/')}/Coverage?patient={Uri.EscapeDataString(patientId!)}&status=active";
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
            if (request.FhirAuthorization?.AccessToken is { Length: > 0 } token)
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.CreateClient(HttpClientName).SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CRD could not read the EHR's Coverage: the EHR answered {Status}", (int)response.StatusCode);
                return new(null, true);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var coverage = CrdPrefetch.ActiveCoverageIn(document.RootElement);
            return new(coverage?.Clone(), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "CRD could not read the EHR's Coverage");
            return new(null, true);
        }
    }
}
