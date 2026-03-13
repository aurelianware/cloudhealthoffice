using System.Text.Json;
using FluentAssertions;
using SmartAuthService.Models;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Unit tests for SMART scope and configuration model correctness.
/// These don't require a running server.
/// </summary>
public class SmartConfigurationTests
{
    // ── SmartScopes constant coverage ─────────────────────────────────────────

    [Theory]
    [InlineData("Patient",               true)]
    [InlineData("Coverage",              true)]
    [InlineData("ExplanationOfBenefit",  true)]
    [InlineData("Encounter",             true)]
    [InlineData("Claim",                 true)]
    [InlineData("Observation",           false)]  // not served by this FHIR server
    public void SmartScopes_ForResource_ReturnsCorrectScopes(string resourceType, bool hasScopes)
    {
        var scopes = SmartScopes.ForResource(resourceType).ToList();

        if (hasScopes)
        {
            scopes.Should().NotBeEmpty();
            // Each known resource has patient, user, and system scopes plus wildcards
            scopes.Should().Contain(SmartScopes.PatientWildcardRead);
            scopes.Should().Contain(SmartScopes.UserWildcardRead);
            scopes.Should().Contain(SmartScopes.SystemWildcardRead);
            scopes.Should().Contain($"patient/{resourceType}.read");
            scopes.Should().Contain($"user/{resourceType}.read");
            scopes.Should().Contain($"system/{resourceType}.read");
        }
        else
        {
            // Unknown resources still get wildcard scopes
            scopes.Should().Contain(SmartScopes.PatientWildcardRead);
            scopes.Should().NotContain($"patient/{resourceType}.read");
        }
    }

    [Fact]
    public void SmartScopes_PatientWildcard_ConstantIsCorrect()
    {
        SmartScopes.PatientWildcardRead.Should().Be("patient/*.read");
        SmartScopes.UserWildcardRead.Should().Be("user/*.read");
        SmartScopes.SystemWildcardRead.Should().Be("system/*.read");
    }

    [Fact]
    public void SmartClaims_Constants_AreCorrect()
    {
        SmartClaims.Patient.Should().Be("patient");
        SmartClaims.Encounter.Should().Be("encounter");
        SmartClaims.FhirUser.Should().Be("fhirUser");
    }

    // ── LaunchContext model ────────────────────────────────────────────────────

    [Fact]
    public void RegisterLaunchRequest_RequiredClientId()
    {
        var req = new RegisterLaunchRequest
        {
            PatientId = "pat-001",
            ClientId = "test-app"
        };

        req.PatientId.Should().Be("pat-001");
        req.ClientId.Should().Be("test-app");
        req.EncounterId.Should().BeNull();
    }

    [Fact]
    public void LaunchContext_DefaultsCreatedAtToNow()
    {
        var ctx = new LaunchContext
        {
            LaunchToken = "tok",
            ClientId = "app",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };

        ctx.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        ctx.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    // ── Scope analysis helper logic ───────────────────────────────────────────

    [Theory]
    [InlineData("patient/*.read",                    "Patient",              true)]
    [InlineData("patient/*.read",                    "ExplanationOfBenefit", true)]
    [InlineData("patient/Coverage.read",             "Coverage",             true)]
    [InlineData("patient/Coverage.read",             "Patient",              false)]
    [InlineData("user/*.read",                       "Patient",              true)]
    [InlineData("system/*.read",                     "Claim",                true)]
    [InlineData("patient/ExplanationOfBenefit.read", "ExplanationOfBenefit", true)]
    [InlineData("openid",                            "Patient",              false)]
    public void ScopeAllowsResource_CorrectlyEvaluates(
        string scope, string resourceType, bool expected)
    {
        var scopes = new HashSet<string>(scope.Split(' '));
        var allowed = HasRequiredScope(scopes, resourceType);
        allowed.Should().Be(expected);
    }

    [Theory]
    [InlineData("patient/*.read",   true)]
    [InlineData("patient/Patient.read patient/Coverage.read", true)]
    [InlineData("user/*.read",      false)]   // user-scoped is NOT patient-binding
    [InlineData("system/*.read",    false)]   // system is NOT patient-binding
    [InlineData("openid",           false)]
    public void IsPatientScopedToken_CorrectlyDetects(string scopes, bool expected)
    {
        var scopeSet = new HashSet<string>(scopes.Split(' '));
        var result = IsPatientScoped(scopeSet);
        result.Should().Be(expected);
    }

    // ── Helpers that mirror SmartScopeEnforcementMiddleware's private logic ──

    private static bool HasRequiredScope(HashSet<string> scopes, string resourceType)
    {
        if (scopes.Contains("patient/*.read") || scopes.Contains("user/*.read") ||
            scopes.Contains("system/*.read"))
            return true;

        return scopes.Contains($"patient/{resourceType}.read")
            || scopes.Contains($"user/{resourceType}.read")
            || scopes.Contains($"system/{resourceType}.read");
    }

    private static bool IsPatientScoped(HashSet<string> scopes)
    {
        if (scopes.Contains("user/*.read") || scopes.Contains("system/*.read"))
            return false;

        return scopes.Contains("patient/*.read")
            || scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal));
    }
}
