using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.BenefitExclusion;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// D-02. The internal "record automated decision" path: only fhir-service's
/// service token may use it; the tenant and the actor come from that token;
/// the request and the decision are stored with a receipt entry and a
/// decision entry. The public create keeps clearing body-supplied outcomes
/// (pinned by <c>AuthorizationAuthenticationTests.Submit_IgnoresBodyTenantActorAndReviewOutcome</c>).
/// </summary>
public class AutomatedDecisionEndpointTests : IClassFixture<AuthorizationApiFactory>
{
    private const string Path = "/api/internal/authorizations/automated-decisions";
    private const string Tenant = "tenant-a";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly AuthorizationApiFactory _factory;
    private readonly IAuthorizationRepository _repo;
    private Authorization? _stored;

    public AutomatedDecisionEndpointTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _repo = factory.AuthorizationRepository;
        _repo.ClearReceivedCalls();
        _repo.GetByAuthorizationNumberAsync(Arg.Any<string>()).Returns((Authorization?)null);
        _repo.CreateAsync(Arg.Do<Authorization>(a => _stored = a)).Returns(call => call.Arg<Authorization>());
    }

    private HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string ServiceToken(string clientId, string tenant = Tenant)
        => ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant);

    internal static object Body(
        string outcome = "Approved",
        string number = "PAS-20261011-AAAA0001",
        DateTime? receivedAt = null,
        bool withApprovedPeriod = true,
        string? tenantInBody = null)
    {
        var received = receivedAt ?? DateTime.UtcNow.AddSeconds(-2);
        return new
        {
            tenantId = tenantInBody,            // not a field: must be ignored
            createdBy = "attacker",             // not a field: must be ignored
            status = "Approved",                // not a field: must be ignored
            authorizationNumber = number,
            receivedAt = received,
            memberId = "MBR-778899",
            patientReference = "Patient/pat-001",
            patientFirstName = "Ana",
            patientLastName = "Rivera",
            patientDateOfBirth = new DateTime(1984, 3, 7, 0, 0, 0, DateTimeKind.Utc),
            lineOfBusiness = "Medicaid",
            requestingProviderNPI = "1234567893",
            serviceTypeCode = "2",
            levelOfService = "U",
            requestedServiceDateFrom = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc),
            requestedServiceDateTo = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc),
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "27447",
                    productOrServiceSystem = "http://www.ama-assn.org/go/cpt",
                    requestedUnits = 1,
                    approvedUnits = 999,     // line outcomes are dropped
                    serviceStatus = "A1",
                },
            },
            decision = new
            {
                outcome,
                denialReasonCode = outcome == "Denied" ? "NOT_COVERED" : null,
                denialReason = outcome == "Denied" ? "Service 27447 is not a covered benefit" : null,
                pendReason = outcome == "Pended" ? "Pended for clinical review." : null,
                approvedServiceDateFrom = withApprovedPeriod ? new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc) : (DateTime?)null,
                approvedServiceDateTo = withApprovedPeriod ? new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc) : (DateTime?)null,
                decidedAt = received.AddSeconds(1),
                rule = "AutoApproveList",
            },
        };
    }

    // ── Who may call it ────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(Path, Body(), Json);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task UserTokenWithDecidePermission_Returns403()
    {
        // A UM coordinator holds authorizations:write and :decide; still not this path.
        using var client = ClientWithToken(_factory.IssueToken(Tenant, ChoRolePermissions.UMCoordinator));
        var response = await client.PostAsJsonAsync(Path, Body(), Json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task OtherServiceToken_Returns403()
    {
        using var client = ClientWithToken(ServiceToken("claims-service"));
        var response = await client.PostAsJsonAsync(Path, Body(), Json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task UserTokenNamedFhirService_Returns403()
    {
        // A user issuer cannot mint a service identity by choosing the subject.
        var token = ChoDevelopmentAuth.UserTokenIssuer()
            .IssueUserToken("fhir-service", Tenant, new[] { ChoServiceRole.Name, ChoRolePermissions.TenantAdmin });
        using var client = ClientWithToken(token);
        var response = await client.PostAsJsonAsync(Path, Body(), Json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── What it stores ─────────────────────────────────────────────────

    [Fact]
    public async Task FhirServiceToken_StoresRequestAndDecision_TenantAndActorFromToken()
    {
        var receivedAt = DateTime.UtcNow.AddSeconds(-3);
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(
            Path, Body(receivedAt: receivedAt, tenantInBody: "tenant-b"), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(_stored);
        Assert.Equal(Tenant, _stored!.TenantId);
        Assert.Equal("fhir-service", _stored.CreatedBy);
        Assert.Equal("fhir-service", _stored.LastUpdatedBy);
        Assert.Equal(AuthorizationStatus.Approved, _stored.Status);
        Assert.Equal("A1", _stored.ReviewDecision);
        Assert.Equal(new DateTime(2026, 11, 2), _stored.ApprovedServiceDateFrom);
        Assert.Equal(new DateTime(2026, 11, 4), _stored.ApprovedServiceDateTo);
        Assert.Equal(_stored.ApprovedServiceDateTo, _stored.ExpirationDate);
        Assert.Equal(receivedAt, _stored.SubmittedDate, TimeSpan.FromMilliseconds(1));
        Assert.Equal("U", _stored.LevelOfService);
        Assert.Equal("2", _stored.ServiceTypeCode);
        Assert.Equal(AuthorizationType.PreAuthorization, _stored.AuthorizationType);
        Assert.Equal("Patient/pat-001", _stored.PatientReference);
        var line = Assert.Single(_stored.RequestedServices);
        Assert.Equal("http://www.ama-assn.org/go/cpt", line.ProductOrServiceSystem);
        Assert.Null(line.ApprovedUnits);
        Assert.Null(line.ServiceStatus);

        Assert.Collection(_stored.StatusHistory,
            receipt =>
            {
                Assert.Equal(AuthorizationStatus.Submitted, receipt.Status);
                Assert.Equal(_stored.SubmittedDate, receipt.ChangedAt);
                Assert.Equal("fhir-service", receipt.ChangedBy);
            },
            decided =>
            {
                Assert.Equal(AuthorizationStatus.Approved, decided.Status);
                Assert.Equal("A1", decided.ReviewDecision);
                Assert.Equal("fhir-service", decided.ChangedBy);
                Assert.Equal("automated:AutoApproveList", decided.Source);
            });
    }

    [Fact]
    public async Task Denial_KeepsCodeAndReason()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(outcome: "Denied", withApprovedPeriod: false), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(AuthorizationStatus.Denied, _stored!.Status);
        Assert.Equal("A3", _stored.ReviewDecision);
        Assert.Equal("NOT_COVERED", _stored.DenialReasonCode);
        Assert.Equal("Service 27447 is not a covered benefit", _stored.DenialReason);
        Assert.Equal("Service 27447 is not a covered benefit", _stored.StatusHistory[^1].Reason);
        Assert.NotNull(_stored.ReviewedDate);
    }

    [Fact]
    public async Task Pend_IsStoredAsPendedWithoutReviewDate()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(outcome: "Pended", withApprovedPeriod: false), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(AuthorizationStatus.Pended, _stored!.Status);
        Assert.Equal("A4", _stored.ReviewDecision);
        Assert.Null(_stored.ReviewedDate);
    }

    // ── What it refuses ────────────────────────────────────────────────

    [Fact]
    public async Task ExistingNumber_Returns409()
    {
        _repo.GetByAuthorizationNumberAsync("PAS-DUP").Returns(new Authorization
        {
            TenantId = Tenant, AuthorizationNumber = "PAS-DUP",
        });
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(number: "PAS-DUP"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task ApprovalWithoutPeriod_Returns400()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(withApprovedPeriod: false), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task DenialWithApprovedPeriod_Returns400()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(outcome: "Denied"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BackdatedReceipt_Returns400()
    {
        // The receipt starts the decision clock; an old one would backdate it.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(receivedAt: DateTime.UtcNow.AddDays(-3)), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MissingNpi_Returns400()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var json = JsonSerializer.Serialize(Body(), Json).Replace("\"1234567893\"", "\"\"");
        var response = await client.PostAsync(Path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Backend: a plan exclusion outranks the automated outcome ───────

    [Fact]
    public async Task Backend_ExcludedService_IsRecordedAsDeniedEvenIfApproved()
    {
        var repository = Substitute.For<IAuthorizationRepository>();
        repository.CreateAsync(Arg.Any<Authorization>()).Returns(call => call.Arg<Authorization>());
        var exclusions = Substitute.For<IAuthorizationExclusionService>();
        exclusions.DetermineExclusion(Arg.Any<Authorization>()).Returns(new BenefitExclusionDetermination
        {
            IsExcluded = true,
            RequestedCode = "J1234",
            MatchedExclusion = new BenefitExclusion { Code = "J1234", ReasonCode = ExclusionReasonCode.NonCoveredBenefit },
        });
        var backend = new ChoAuthorizationBackend(repository, exclusions);
        var received = DateTime.UtcNow.AddSeconds(-1);

        var stored = await backend.RecordAutomatedDecisionAsync(
            new Authorization { SubmittedDate = received, CreatedBy = "fhir-service" },
            new AutomatedDecision
            {
                Outcome = AutomatedDecisionOutcome.Approved,
                ApprovedServiceDateFrom = DateTime.UtcNow.Date,
                DecidedAt = DateTime.UtcNow,
            });

        Assert.Equal(AuthorizationStatus.Denied, stored.Status);
        Assert.Equal("A3", stored.ReviewDecision);
        Assert.Equal(ExclusionReasonCode.NonCoveredBenefit, stored.DenialReasonCode);
        Assert.Null(stored.ApprovedServiceDateFrom);
        Assert.Equal("benefit-exclusion", stored.StatusHistory[^1].Source);
    }
}
