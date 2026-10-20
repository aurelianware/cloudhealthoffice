using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed prior-authorization validation client
/// (<c>GET /api/authorizations/{number}/validate</c> on authorization-service).
///
/// <para>
/// Returns a typed <see cref="AuthorizationLookupResult"/>, never null
/// (D-04): only two responses are definitive — a 200 with a readable body
/// for this number (<see cref="AuthorizationLookupStatus.Valid"/> /
/// <see cref="AuthorizationLookupStatus.Invalid"/>) and authorization-service's
/// own 404 for the number (<see cref="AuthorizationLookupStatus.NotFound"/>).
/// Everything else — transport errors, timeouts, 5xx, 401/403 from a
/// misconfigured service token, a 404 that is not the service's answer
/// (gateway, wrong base address), an unreadable body — is
/// <see cref="AuthorizationLookupStatus.Unavailable"/>, and adjudication
/// pends on it (owner decision Q-9).
/// </para>
/// </summary>
public sealed class HttpAuthorizationValidationClient : IAuthorizationValidationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpAuthorizationValidationClient> _logger;

    public HttpAuthorizationValidationClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpAuthorizationValidationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AuthorizationLookupResult> ValidateAsync(
        string tenantId,
        string authorizationNumber,
        string? procedureCode,
        DateTime serviceDate,
        string? providerNpi,
        string memberId,
        CancellationToken ct = default,
        int? authorizationLineNumber = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(authorizationNumber))
        {
            // Callers decide "required but not provided" themselves; a blank
            // number names no authorization, so it cannot be valid.
            return AuthorizationLookupResult.NotFound();
        }

        if (string.IsNullOrWhiteSpace(memberId))
            return AuthorizationLookupResult.Unavailable("claim member identity is missing");

        var member = memberId.Trim();
        var number = authorizationNumber.Trim();
        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.AuthorizationService);
            var serviceDateQuery = serviceDate.ToUniversalTime()
                .ToString("o", CultureInfo.InvariantCulture);
            var url = $"/api/authorizations/{Uri.EscapeDataString(number)}/validate" +
                      $"?serviceDate={Uri.EscapeDataString(serviceDateQuery)}&memberId={Uri.EscapeDataString(member)}";

            if (!string.IsNullOrWhiteSpace(procedureCode))
            {
                url += $"&procedureCode={Uri.EscapeDataString(procedureCode.Trim())}";
            }

            if (!string.IsNullOrWhiteSpace(providerNpi))
            {
                url += $"&providerNpi={Uri.EscapeDataString(providerNpi.Trim())}";
            }

            if (authorizationLineNumber.HasValue)
            {
                if (authorizationLineNumber <= 0 || string.IsNullOrWhiteSpace(procedureCode))
                    return AuthorizationLookupResult.Unavailable("authorization line reference requires a positive number and procedure code");
                url += $"&lineNumber={authorizationLineNumber.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            // Always claims-service's own identity, never the caller's (#1299
            // B1): an examiner approval re-run runs inside the examiner's
            // request, and ClaimsExaminer / ClaimsSupervisor do not hold
            // authorizations:read — forwarding their token made every lookup
            // a 403, so no examiner could clear a PA-required claim. The
            // token is minted for the adjudication tenant named here.
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (IsServiceNotFound(body, number))
                {
                    return AuthorizationLookupResult.NotFound();
                }

                return Unavailable(
                    "authorization-service returned 404 without its not-found answer for the number",
                    number, tenantId, response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(
                    $"authorization-service returned {(int)response.StatusCode}",
                    number, tenantId, response.StatusCode);
            }

            var validation = await response.Content
                .ReadFromJsonAsync<AuthorizationValidationResult>(JsonOptions, ct)
                .ConfigureAwait(false);

            if (validation is null)
            {
                return Unavailable("authorization-service returned an empty validation body", number, tenantId, response.StatusCode);
            }

            // A body for another number is not an answer about this one.
            if (!string.Equals(validation.AuthorizationNumber?.Trim(), number, StringComparison.OrdinalIgnoreCase))
            {
                return Unavailable("authorization-service answered for a different authorization number", number, tenantId, response.StatusCode);
            }

            // Require proof that this producer compared the requested individual.
            // An old producer silently ignoring memberId is not a bound answer.
            if (!string.Equals(validation.ValidatedMemberId, member, StringComparison.Ordinal)
                || !validation.MemberMatches.HasValue)
                return AuthorizationLookupResult.Unavailable("authorization member comparison was not confirmed");

            if (!validation.MemberMatches.Value)
                return AuthorizationLookupResult.Invalid(validation, "Authorization does not belong to this claim's member");

            // Ambiguous matching is not a clinical rejection. The message fallback
            // supports the previous producer during a rolling deployment.
            if (validation.ValidationCode == "AmbiguousServiceLine"
                || validation.ValidationMessage == "Procedure occurs on multiple lines; specify lineNumber")
                return AuthorizationLookupResult.Unavailable("authorization service line is ambiguous; provide the authorization line reference");

            return validation.IsValid
                ? AuthorizationLookupResult.Valid(validation)
                : AuthorizationLookupResult.Invalid(
                    validation,
                    string.IsNullOrWhiteSpace(validation.ValidationMessage)
                        ? "Prior authorization is not valid for this claim"
                        : validation.ValidationMessage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (shutdown, lock lost): not a lookup result.
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure — transport, HttpClient timeout (a
            // TaskCanceledException not caused by ct), unreadable JSON, a
            // misconfigured client — is not an answer.
            _logger.LogWarning(
                ex,
                "Authorization validation lookup failed for authorization {AuthorizationNumber} tenant {Tenant}",
                LogSanitizer.Sanitize(number),
                LogSanitizer.Sanitize(tenantId));
            return AuthorizationLookupResult.Unavailable($"authorization lookup failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// authorization-service's own 404 for an unknown number is
    /// <c>NotFound($"Authorization {authNumber} not found")</c> (plain text,
    /// or a JSON string), pinned on the producer side by
    /// <c>AuthorizationAuthenticationTests.Validate_UnknownNumber_Returns404WithTheNotFoundBody</c>. Any other 404 — an ingress, a wrong base address,
    /// a route that moved — says nothing about the number.
    /// </summary>
    internal static bool IsServiceNotFound(string? body, string number)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;

        // With Accept: application/json the service writes the message as a
        // JSON string, whose escapes (\u0022 …) must be decoded before matching.
        var text = body.Trim();
        if (text.StartsWith('"'))
        {
            try
            {
                text = JsonSerializer.Deserialize<string>(text) ?? string.Empty;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return string.Equals(text, $"Authorization {number} not found", StringComparison.OrdinalIgnoreCase);
    }

    private AuthorizationLookupResult Unavailable(
        string reason, string number, string tenantId, HttpStatusCode status)
    {
        _logger.LogWarning(
            "Authorization validation for {AuthorizationNumber} tenant {Tenant} not definitive ({StatusCode}): {Reason}",
            LogSanitizer.Sanitize(number),
            LogSanitizer.Sanitize(tenantId),
            (int)status,
            reason);
        return AuthorizationLookupResult.Unavailable(reason);
    }
}
