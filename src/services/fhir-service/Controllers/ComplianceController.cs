using FhirService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// CMS-0057-F compliance self-assessment endpoint.
/// Returns a structured report of which CMS-0057-F requirements are met/unmet
/// for the current tenant — a key differentiator for CHO health plans.
/// </summary>
[Route("fhir/r4")]
[Authorize]
public class ComplianceController : FhirControllerBase
{
    private readonly IConfiguration _config;
    private readonly ICms0057ComplianceChecker _complianceChecker;

    public ComplianceController(IConfiguration config, ICms0057ComplianceChecker complianceChecker)
    {
        _config = config;
        _complianceChecker = complianceChecker;
    }

    /// <summary>
    /// GET /fhir/r4/compliance-status
    /// Returns a structured report of CMS-0057-F compliance posture for the current tenant.
    /// </summary>
    [HttpGet("compliance-status")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(Cms0057ComplianceReport), 200)]
    public IActionResult GetComplianceStatus()
    {
        var tenantId = TenantId;

        var patientAccessCheck = CheckPatientAccessApi();
        var providerDirectoryCheck = CheckProviderDirectoryApi();
        var priorAuthCheck = CheckPriorAuthorizationApi();
        var payerToPayerCheck = CheckPayerToPayerExchange();
        var smartScopesCheck = CheckSmartOnFhirScopes();

        var requirements = new List<Cms0057Requirement>
        {
            patientAccessCheck,
            providerDirectoryCheck,
            priorAuthCheck,
            payerToPayerCheck,
            smartScopesCheck
        };

        var metCount = requirements.Count(r => r.Met);

        var supportedResourceTypes = _complianceChecker.SupportedResourceTypes;

        var report = new Cms0057ComplianceReport(
            TenantId: tenantId,
            OverallCompliant: requirements.All(r => r.Met),
            RequirementsMet: metCount,
            TotalRequirements: requirements.Count,
            CompliancePercentage: (int)Math.Round(100.0 * metCount / requirements.Count),
            Requirements: requirements,
            SupportedValidationResources: supportedResourceTypes,
            AssessedAt: DateTimeOffset.UtcNow,
            FhirVersion: "4.0.1",
            RuleName: "CMS-0057-F",
            RuleDescription: "CMS Interoperability and Prior Authorization Final Rule");

        return Ok(report);
    }

    /// <summary>
    /// Patient Access API: must be enabled and return valid FHIR R4.
    /// </summary>
    private Cms0057Requirement CheckPatientAccessApi()
    {
        var issues = new List<string>();

        var patientAccessEnabled = _config.GetValue("Cms0057:PatientAccessApi:Enabled", true);
        if (!patientAccessEnabled)
            issues.Add("Patient Access API is not enabled");

        var fhirVersion = _config["Cms0057:PatientAccessApi:FhirVersion"] ?? "4.0.1";
        if (!fhirVersion.StartsWith("4.", StringComparison.Ordinal))
            issues.Add($"FHIR version {fhirVersion} is not R4-compliant; must be 4.x");

        var requiredResources = new[] { "Patient", "Coverage", "ExplanationOfBenefit", "Encounter" };
        var enabledResources = _config.GetSection("Cms0057:PatientAccessApi:Resources")
            .GetChildren().Select(c => c.Value).ToList();

        // If no explicit config, assume all required resources are available (convention over configuration)
        if (enabledResources.Count > 0)
        {
            var missing = requiredResources.Except(enabledResources!, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                issues.Add($"Missing required resources: {string.Join(", ", missing)}");
        }

        return new Cms0057Requirement(
            Id: "CMS-0057-F-01",
            Name: "Patient Access API",
            Description: "Patient Access API is enabled and returns valid FHIR R4 resources (Patient, Coverage, EOB, Encounter)",
            Met: issues.Count == 0,
            Issues: issues);
    }

    /// <summary>
    /// Provider Directory API: must be enabled.
    /// </summary>
    private Cms0057Requirement CheckProviderDirectoryApi()
    {
        var issues = new List<string>();

        var enabled = _config.GetValue("Cms0057:ProviderDirectoryApi:Enabled", true);
        if (!enabled)
            issues.Add("Provider Directory API is not enabled");

        // Program.cs registers NppesApi HttpClient with a default base URL when Nppes:BaseUrl
        // is absent, so the provider directory is functional even without explicit config.
        // Only flag as non-compliant if the config explicitly disables NPPES.
        var nppesDisabled = _config.GetValue("Nppes:Disabled", false);
        if (nppesDisabled)
            issues.Add("NPPES integration is explicitly disabled for provider directory lookups");

        return new Cms0057Requirement(
            Id: "CMS-0057-F-02",
            Name: "Provider Directory API",
            Description: "Provider Directory API is enabled and supports provider lookups",
            Met: issues.Count == 0,
            Issues: issues);
    }

    /// <summary>
    /// Prior Authorization API: must support required operations ($submit, $inquire, status polling).
    /// </summary>
    private Cms0057Requirement CheckPriorAuthorizationApi()
    {
        var issues = new List<string>();

        var enabled = _config.GetValue("Cms0057:PriorAuthorizationApi:Enabled", true);
        if (!enabled)
        {
            issues.Add("Prior Authorization API is not enabled");
            return new Cms0057Requirement(
                Id: "CMS-0057-F-03",
                Name: "Prior Authorization API",
                Description: "Prior Authorization API supports $submit, $inquire operations and status polling per Da Vinci PAS",
                Met: false,
                Issues: issues);
        }

        var requiredOperations = new[] { "$submit", "$inquire" };
        var supportedOperations = _config.GetSection("Cms0057:PriorAuthorizationApi:Operations")
            .GetChildren().Select(c => c.Value).ToList();

        if (supportedOperations.Count == 0)
        {
            issues.Add($"Required operations are not explicitly configured: {string.Join(", ", requiredOperations)}");
        }
        else
        {
            var missing = requiredOperations.Except(supportedOperations!, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                issues.Add($"Missing required operations: {string.Join(", ", missing)}");
        }

        var timelineEnforcement = _config.GetValue("Cms0057:PriorAuthorizationApi:TimelineEnforcement", true);
        if (!timelineEnforcement)
            issues.Add("Timeline enforcement (72h urgent / 7d standard) is not enabled");

        return new Cms0057Requirement(
            Id: "CMS-0057-F-03",
            Name: "Prior Authorization API",
            Description: "Prior Authorization API supports $submit, $inquire operations and status polling per Da Vinci PAS",
            Met: issues.Count == 0,
            Issues: issues);
    }

    /// <summary>
    /// Payer-to-Payer data exchange: must be configured.
    /// </summary>
    private Cms0057Requirement CheckPayerToPayerExchange()
    {
        var issues = new List<string>();

        var enabled = _config.GetValue("Cms0057:PayerToPayerExchange:Enabled", false);
        if (!enabled)
            issues.Add("Payer-to-Payer data exchange is not enabled");

        var hasEndpoint = !string.IsNullOrEmpty(_config["Cms0057:PayerToPayerExchange:Endpoint"]);
        if (!hasEndpoint)
            issues.Add("Payer-to-Payer exchange endpoint is not configured");

        return new Cms0057Requirement(
            Id: "CMS-0057-F-04",
            Name: "Payer-to-Payer Data Exchange",
            Description: "Payer-to-Payer data exchange is configured for member transitions between health plans",
            Met: issues.Count == 0,
            Issues: issues);
    }

    /// <summary>
    /// SMART on FHIR scopes: required scopes must be registered.
    /// </summary>
    private Cms0057Requirement CheckSmartOnFhirScopes()
    {
        var issues = new List<string>();

        var requiredScopes = new[]
        {
            "patient/Patient.read",
            "patient/Coverage.read",
            "patient/ExplanationOfBenefit.read",
            "user/Patient.read",
            "user/Coverage.read",
            "user/ExplanationOfBenefit.read",
            "launch/patient"
        };

        var registeredScopes = _config.GetSection("Cms0057:SmartScopes:Registered")
            .GetChildren().Select(c => c.Value).ToList();

        if (registeredScopes.Count > 0)
        {
            var missing = requiredScopes.Except(registeredScopes!, StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                issues.Add($"Missing required SMART scopes: {string.Join(", ", missing)}");
        }

        // Check that SMART configuration endpoint exists
        var smartConfigured = !string.IsNullOrEmpty(_config["SmartAuth:Issuer"]);
        if (!smartConfigured)
            issues.Add("SMART on FHIR authorization server (SmartAuth:Issuer) is not configured");

        return new Cms0057Requirement(
            Id: "CMS-0057-F-05",
            Name: "SMART on FHIR Scopes",
            Description: "Required SMART on FHIR scopes are registered for patient and user access",
            Met: issues.Count == 0,
            Issues: issues);
    }
}

// ── Response DTOs ────────────────────────────────────────────────────────────

public record Cms0057ComplianceReport(
    string TenantId,
    bool OverallCompliant,
    int RequirementsMet,
    int TotalRequirements,
    int CompliancePercentage,
    IReadOnlyList<Cms0057Requirement> Requirements,
    IReadOnlyList<string> SupportedValidationResources,
    DateTimeOffset AssessedAt,
    string FhirVersion,
    string RuleName,
    string RuleDescription);

public record Cms0057Requirement(
    string Id,
    string Name,
    string Description,
    bool Met,
    IReadOnlyList<string> Issues);
