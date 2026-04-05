using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Observability;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci PAS (Prior Authorization Support) controller.
/// Implements the synchronous Claim/$submit operation per PAS IG v2.1.0.
/// Target: respond within 15 seconds including network time.
/// </summary>
[Route("fhir/r4")]
[Authorize]
public class PasController : FhirControllerBase
{
    private readonly IPasAutoAdjudicator _adjudicator;
    private readonly PasResponseBuilder _responseBuilder;
    private readonly ICms0057ComplianceChecker _complianceChecker;
    private readonly PasAutoAdjudicationConfig _config;
    private readonly HttpClient _authServiceClient;
    private readonly HttpClient _providerVerificationClient;
    private readonly ILogger<PasController> _logger;

    public PasController(
        IPasAutoAdjudicator adjudicator,
        PasResponseBuilder responseBuilder,
        ICms0057ComplianceChecker complianceChecker,
        IOptions<PasAutoAdjudicationConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<PasController> logger)
    {
        _adjudicator = adjudicator;
        _responseBuilder = responseBuilder;
        _complianceChecker = complianceChecker;
        _config = config.Value;
        _authServiceClient = httpClientFactory.CreateClient("AuthorizationService");
        _providerVerificationClient = httpClientFactory.CreateClient("ProviderVerificationService");
        _logger = logger;
    }

    /// <summary>
    /// Da Vinci PAS Claim/$submit
    /// Synchronous prior authorization request/response.
    /// Target: respond within 15 seconds per PAS IG 2.1.0 Section 5.2.1.
    /// </summary>
    [HttpPost("Claim/$submit")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ClaimSubmit([FromBody] Bundle requestBundle)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Validate and extract the PAS Claim from the request bundle
            var (validatedClaim, validationError) = ValidateAndExtractClaim(requestBundle);
            if (validationError != null)
            {
                return validationError;
            }
            var claim = validatedClaim!;

            _logger.LogInformation(
                "PAS $submit received for tenant {TenantId}, claim type {ClaimType}",
                SanitizeForLog(TenantId),
                claim.Type?.Coding?.FirstOrDefault()?.Code ?? "unknown");

            // 2. Validate compliance
            var compliance = _complianceChecker.ValidateCompliance(claim);
            if (!compliance.Compliant)
            {
                var issues = string.Join("; ", compliance.Issues.Select(i => i.Message));
                _logger.LogWarning("PAS $submit compliance validation failed: {Issues}", issues);
                return FhirBadRequest($"Claim does not meet CMS-0057-F compliance requirements: {issues}");
            }

            // 2.5 Provider verification pre-check
            var providerNpi = ExtractProviderNpi(claim);
            if (!string.IsNullOrEmpty(providerNpi))
            {
                var verificationResult = await CheckProviderVerificationAsync(providerNpi, HttpContext.RequestAborted);
                if (verificationResult.IsExcluded)
                {
                    var deniedDecision = new PasDecisionResult
                    {
                        HasDecision = true,
                        Decision = "denied",
                        DenialReasonCode = "PROVIDER_EXCLUDED",
                        DenialReason = $"Provider NPI {providerNpi} is excluded from federal healthcare programs." +
                                       (string.IsNullOrWhiteSpace(verificationResult.ExclusionSource)
                                           ? string.Empty
                                           : $" Source: {verificationResult.ExclusionSource}"),
                        RuleName = "provider-exclusion-check",
                    };
                    var deniedBundle = _responseBuilder.BuildDeniedResponse(claim, deniedDecision);
                    await PersistAuthorizationAsync(claim, deniedDecision);
                    RecordMetrics(sw, "denied", "provider-exclusion-check");

                    _logger.LogWarning(
                        "PAS $submit auto-denied: excluded provider NPI {Npi}, source={Source}",
                        SanitizeForLog(providerNpi), verificationResult.ExclusionSource);

                    return Ok(deniedBundle);
                }

                if (verificationResult.IntegrityScore >= 0 && verificationResult.IntegrityScore < 40)
                {
                    _logger.LogWarning(
                        "PAS $submit: provider NPI {Npi} has low integrity score {Score} ({Rating})",
                        SanitizeForLog(providerNpi), verificationResult.IntegrityScore, verificationResult.Rating);
                }
            }

            // 3. Auto-adjudicate with time budget
            var timeBudgetMs = _config.MaxResponseMs;
            var decision = await _adjudicator.TryDecideAsync(
                claim, requestBundle, timeBudgetMs, HttpContext.RequestAborted);

            // 4. Build response based on decision
            Bundle responseBundle;
            if (decision.HasDecision && decision.Decision == "approved")
            {
                responseBundle = _responseBuilder.BuildApprovedResponse(claim, decision);
                await PersistAuthorizationAsync(claim, decision);
                RecordMetrics(sw, "approved", decision.RuleName);
            }
            else if (decision.HasDecision && decision.Decision == "denied")
            {
                responseBundle = _responseBuilder.BuildDeniedResponse(claim, decision);
                await PersistAuthorizationAsync(claim, decision);
                RecordMetrics(sw, "denied", decision.RuleName);
            }
            else
            {
                responseBundle = _responseBuilder.BuildPendedResponse(claim);
                await PersistPendedAuthorizationAsync(claim);
                RecordMetrics(sw, "pended", decision.RuleName);
            }

            _logger.LogInformation(
                "PAS $submit completed in {ElapsedMs}ms, decision={Decision}, rule={Rule}",
                sw.ElapsedMilliseconds, decision.Decision ?? "pended", decision.RuleName);

            return Ok(responseBundle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PAS $submit failed after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            RecordMetrics(sw, "error", "exception");
            return FhirUnprocessable($"Internal error processing PAS request: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task PersistAuthorizationAsync(Claim claim, PasDecisionResult decision)
    {
        try
        {
            var authPayload = new
            {
                tenantId = TenantId,
                authorizationNumber = decision.AuthorizationNumber ?? $"PAS-{Guid.NewGuid():N}",
                memberId = claim.Patient?.Reference ?? "",
                patientFirstName = "PAS",
                patientLastName = "Patient",
                patientDateOfBirth = DateTime.UtcNow.AddYears(-30),
                lineOfBusiness = "Commercial",
                requestingProviderNPI = claim.Provider?.Identifier?.Value ?? "",
                authorizationType = "PreAuthorization",
                serviceTypeCode = claim.Type?.Coding?.FirstOrDefault()?.Code ?? "1",
                requestedServiceDateFrom = DateTime.UtcNow,
                status = decision.Decision == "approved" ? "Approved" :
                         decision.Decision == "denied" ? "Denied" : "Pended",
                reviewDecision = decision.Decision == "approved" ? "A1" :
                                 decision.Decision == "denied" ? "A3" : "A4",
                requestedServices = new[] { new { procedureCode = "99213", requestedUnits = 1 } },
            };

            await _authServiceClient.PostAsJsonAsync("api/authorizations", authPayload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist PAS authorization — response still returned to caller");
        }
    }

    private async System.Threading.Tasks.Task PersistPendedAuthorizationAsync(Claim claim)
    {
        await PersistAuthorizationAsync(claim, new PasDecisionResult
        {
            HasDecision = false,
            Decision = "pended",
        });
    }

    private static void RecordMetrics(Stopwatch sw, string decision, string? rule)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("pas.decision", decision),
            new("pas.rule", rule ?? "none"),
        };

        ChoMetrics.PasSubmitDuration.Record(sw.Elapsed.TotalSeconds, tags);
        ChoMetrics.PasSubmitDecisions.Add(1, tags);
    }

    // ── Input validation ─────────────────────────────────────────────────────

    private static readonly HashSet<string> AllowedResourceTypes = new(StringComparer.Ordinal)
    {
        "Claim", "Patient", "Coverage", "Practitioner",
        "Organization", "Condition", "Observation", "ServiceRequest",
        "DocumentReference", "QuestionnaireResponse"
    };

    private static readonly Regex RelativeReferencePattern = new(
        @"^[A-Za-z]+/[A-Za-z0-9\-\.]+$", RegexOptions.Compiled);

    private const int MaxBundleEntries = 50;

    private (Claim? claim, IActionResult? error) ValidateAndExtractClaim(Bundle? requestBundle)
    {
        if (requestBundle?.Entry == null || requestBundle.Entry.Count == 0)
        {
            return (null, FhirBadRequest("Request bundle must contain at least one entry"));
        }

        if (requestBundle.Entry.Count > MaxBundleEntries)
        {
            return (null, FhirBadRequest(
                $"Request bundle exceeds maximum of {MaxBundleEntries} entries"));
        }

        // Reject unexpected resource types
        foreach (var entry in requestBundle.Entry)
        {
            if (entry.Resource != null && !AllowedResourceTypes.Contains(entry.Resource.TypeName))
            {
                _logger.LogWarning("PAS $submit rejected unexpected resource type: {Type}",
                    SanitizeForLog(entry.Resource.TypeName));
                return (null, FhirBadRequest(
                    $"Bundle contains disallowed resource type: {entry.Resource.TypeName}"));
            }
        }

        var claim = requestBundle.Entry
            .Select(e => e.Resource)
            .OfType<Claim>()
            .FirstOrDefault();

        if (claim == null)
        {
            return (null, FhirBadRequest("Request bundle must contain a Claim resource"));
        }

        if (claim.Provider == null || claim.Patient == null ||
            claim.Insurance == null || claim.Insurance.Count == 0)
        {
            return (null, FhirBadRequest(
                "Claim must include provider, patient, and insurance references"));
        }

        // Ensure references are relative (e.g. "Patient/123"), not absolute URLs,
        // to prevent SSRF when references are forwarded to internal services.
        if (!IsRelativeReference(claim.Patient?.Reference) ||
            !IsRelativeReference(claim.Provider?.Reference) ||
            !IsRelativeReference(claim.Insurer?.Reference))
        {
            return (null, FhirBadRequest(
                "Claim references must be relative FHIR references (e.g. 'Patient/123')"));
        }

        return (claim, null);
    }

    private static bool IsRelativeReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return true; // null/empty handled by required-field checks above
        return RelativeReferencePattern.IsMatch(reference);
    }

    // ── Provider verification ────────────────────────────────────────────────

    private static string? ExtractProviderNpi(Claim claim)
        => claim.Provider?.Identifier?.Value;

    private async Task<ProviderVerificationSummary> CheckProviderVerificationAsync(
        string npi, CancellationToken ct)
    {
        try
        {
            var response = await _providerVerificationClient.GetAsync(
                $"api/v1/providers/{Uri.EscapeDataString(npi)}/integrity-score?tier=Basic", ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProviderIntegrityResponse>(
                    cancellationToken: ct);

                if (result != null)
                {
                    return new ProviderVerificationSummary
                    {
                        IntegrityScore = result.CompositeScore,
                        Rating = result.Rating,
                        IsExcluded = string.Equals(result.Status, "Excluded", StringComparison.OrdinalIgnoreCase),
                        ExclusionSource = result.Flags?
                            .FirstOrDefault(f => f.Code == "EXCLUDED")?.Source,
                        Status = result.Status,
                    };
                }
            }

            _logger.LogWarning(
                "Provider Verification Service returned {StatusCode} for NPI {Npi} — proceeding without verification",
                response.StatusCode, SanitizeForLog(npi));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider Verification Service unavailable for NPI {Npi} — proceeding without verification",
                SanitizeForLog(npi));
        }

        // Graceful degradation: if verification service is down, proceed without blocking
        return new ProviderVerificationSummary
        {
            IntegrityScore = -1,
            Rating = "Unknown",
            IsExcluded = false,
            Status = "Unavailable",
        };
    }
}
