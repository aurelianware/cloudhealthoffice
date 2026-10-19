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

    /// <summary>A SHA-256 of the request payload, as fhir-service sends it.</summary>
    internal const string RequestHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string OtherHash = "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752";

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
        string? tenantInBody = null,
        string? decisionLineOfBusiness = "Medicaid",
        string? requestHash = RequestHash,
        string rule = "AutoApproveList",
        string? stateCode = "TX")
    {
        var received = receivedAt ?? DateTime.UtcNow.AddSeconds(-2);
        return new
        {
            tenantId = tenantInBody,            // not a field: must be ignored
            createdBy = "attacker",             // not a field: must be ignored
            status = "Approved",                // not a field: must be ignored
            authorizationNumber = number,
            requestHash,
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
                rule,
                lineOfBusiness = decisionLineOfBusiness,
                stateCode,
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
        // Hand-forged: the issuer helper strips cho.service from a user token,
        // so only a forged token tests what the server does with one that
        // carries it, with sub = azp = fhir-service.
        var token = Forge(ChoDevelopmentAuth.UserIssuer, "fhir-service", "fhir-service",
            ChoServiceRole.Name, ChoRolePermissions.TenantAdmin);
        using var client = ClientWithToken(token);
        var response = await client.PostAsJsonAsync(Path, Body(), Json);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
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
        Assert.Equal(RequestHash, _stored.RequestHash);
        var line = Assert.Single(_stored.RequestedServices);
        Assert.Equal("http://www.ama-assn.org/go/cpt", line.ProductOrServiceSystem);
        Assert.Equal(line.RequestedUnits, line.ApprovedUnits);
        Assert.Equal("A1", line.ServiceStatus);

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
                Assert.Equal("automated:AutoApproveList;lob=Medicaid;state=TX", decided.Source);
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
    public async Task ExistingNumber_FromAnotherRequest_Returns409()
    {
        _repo.GetByAuthorizationNumberAsync("PAS-DUP").Returns(new Authorization
        {
            TenantId = Tenant, AuthorizationNumber = "PAS-DUP", RequestHash = OtherHash,
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

    [Fact]
    public async Task ExistingNumber_WithNoStoredHash_Returns409()
    {
        _repo.GetByAuthorizationNumberAsync("PAS-OLD").Returns(new Authorization
        {
            TenantId = Tenant, AuthorizationNumber = "PAS-OLD",
        });
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(number: "PAS-OLD"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    // ── Idempotency: the same request recorded twice is one record ─────

    [Fact]
    public async Task ExistingNumber_SameRequestHash_Returns200WithTheExistingRecord()
    {
        // fhir-service timed out after a write that succeeded, and records the
        // same request again: the answer is the record already stored.
        _repo.GetByAuthorizationNumberAsync("PAS-RETRY").Returns(new Authorization
        {
            TenantId = Tenant, AuthorizationNumber = "PAS-RETRY", RequestHash = RequestHash,
            Status = AuthorizationStatus.Approved, ReviewDecision = "A1",
        });
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(number: "PAS-RETRY"), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await response.Content.ReadFromJsonAsync<Authorization>(Json);
        Assert.Equal("PAS-RETRY", stored!.AuthorizationNumber);
        Assert.Equal(AuthorizationStatus.Approved, stored.Status);
        Assert.Equal(RequestHash, stored.RequestHash);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentInsert_UsesTheUniqueWinner_AndOnlyReplaysTheSameHash(bool sameHash)
    {
        var winner = new Authorization
        {
            Id = "winning-record", TenantId = Tenant, AuthorizationNumber = "PAS-RACE",
            RequestHash = sameHash ? RequestHash : OtherHash,
            Status = AuthorizationStatus.Denied, ReviewDecision = "A3",
        };
        _repo.GetByAuthorizationNumberAsync("PAS-RACE").Returns((Authorization?)null, winner);
        _repo.CreateAsync(Arg.Any<Authorization>()).Returns(_ => Task.FromException<Authorization>(
            new global::AuthorizationService.Exceptions.DuplicateAuthorizationNumberException("PAS-RACE")));
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(number: "PAS-RACE"), Json);

        Assert.Equal(sameHash ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        if (sameHash)
        {
            var stored = await response.Content.ReadFromJsonAsync<Authorization>(Json);
            Assert.Equal("winning-record", stored!.Id);
            Assert.Equal(AuthorizationStatus.Denied, stored.Status);
        }
        await _repo.Received(1).CreateAsync(Arg.Any<Authorization>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    public async Task MissingOrMalformedRequestHash_Returns400(string? hash)
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(requestHash: hash), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    // ── The decision's basis must be the record's ──────────────────────

    [Fact]
    public async Task DecisionOnAnotherLineOfBusiness_Returns400()
    {
        // The record says Medicaid; the decision was made on Commercial. The
        // record may not claim a basis the decision did not use.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(decisionLineOfBusiness: "Commercial"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Decision.LineOfBusiness", await response.Content.ReadAsStringAsync());
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task DecisionWithoutLineOfBusiness_Returns400()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(decisionLineOfBusiness: null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Theory]
    [InlineData("provider-exclusion-check", "Denied")]
    [InlineData("ConfigDisabled", "Pended")]
    [InlineData("TimeBudgetExceeded", "Pended")]
    [InlineData("LineOfBusinessUndetermined", "Pended")]
    [InlineData("NoOp", "Pended")]
    public async Task NoEvaluationRule_WithoutLineOfBusiness_IsStored_AndClaimsNoBasis(string rule, string outcome)
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: outcome, withApprovedPeriod: false, decisionLineOfBusiness: null, rule: rule, stateCode: null), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"automated:{rule};lob=n/a;state=none", _stored!.StatusHistory[^1].Source);
    }

    [Theory]
    [InlineData("provider-exclusion-check", "Pended")]
    [InlineData("provider-exclusion-check", "Approved")]
    [InlineData("ConfigDisabled", "Approved")]
    [InlineData("ConfigDisabled", "Denied")]
    [InlineData("TimeBudgetExceeded", "Denied")]
    [InlineData("LineOfBusinessUndetermined", "Approved")]
    [InlineData("NoOp", "Denied")]
    public async Task NoEvaluationRule_WithAnOutcomeItCannotReach_Returns400(string rule, string outcome)
    {
        // A provider exclusion only denies; the no-evaluation pends only pend.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: outcome, withApprovedPeriod: outcome == "Approved", decisionLineOfBusiness: null,
            rule: rule, stateCode: null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task NoEvaluationRule_WithAnOutcomeItCannotReach_Returns400_EvenWithALineOfBusiness()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: "Approved", rule: "ConfigDisabled"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("NoOp", "Medicaid", "TX")]
    [InlineData("NoOp", "Medicaid", null)]
    [InlineData("NoOp", null, "TX")]
    [InlineData("ConfigDisabled", "Medicaid", null)]
    [InlineData("TimeBudgetExceeded", null, "TX")]
    [InlineData("LineOfBusinessUndetermined", "Medicaid", null)]
    public async Task NoEvaluationPend_ClaimingABasis_Returns400(string rule, string? lob, string? state)
    {
        // These pends evaluated nothing; a LOB or state on them (even the
        // record's own LOB) would be stored as a basis the decision did not use.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: "Pended", withApprovedPeriod: false, decisionLineOfBusiness: lob, rule: rule, stateCode: state), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task ProviderExclusion_WithTheRecordsLineOfBusiness_IsStillAccepted()
    {
        // Unchanged for the exclusion: a stated LOB must be the record's.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: "Denied", withApprovedPeriod: false, decisionLineOfBusiness: "Medicaid",
            rule: "provider-exclusion-check", stateCode: null), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task NoEvaluationRule_WithAnotherLineOfBusiness_Returns400()
    {
        // Optional for these rules, but if stated it must still be the record's.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(
            outcome: "Pended", withApprovedPeriod: false, decisionLineOfBusiness: "Commercial", rule: "ConfigDisabled"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── The tracking number ────────────────────────────────────────────

    [Theory]
    [InlineData("PAS 20261011")]
    [InlineData("PAS/20261011")]
    [InlineData("PAS_20261011")]
    [InlineData("PAS-2026\n1011")]
    [InlineData("A123456789B123456789C123456789D123456789E1234567890")]
    public async Task MalformedAuthorizationNumber_Returns400(string number)
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.PostAsJsonAsync(Path, Body(number: number), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    // ── Reading back one record (fhir-service's compensating read) ─────

    [Fact]
    public async Task Get_ByNumber_ReturnsTheRecordInTheTokensTenant()
    {
        _repo.GetByAuthorizationNumberAsync("PAS-20261011-READ0001").Returns(new Authorization
        {
            TenantId = Tenant, AuthorizationNumber = "PAS-20261011-READ0001", RequestHash = RequestHash,
            Status = AuthorizationStatus.Pended,
        });
        using var client = ClientWithToken(ServiceToken("fhir-service"));

        var response = await client.GetAsync($"{Path}/PAS-20261011-READ0001");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await response.Content.ReadFromJsonAsync<Authorization>(Json);
        Assert.Equal(RequestHash, stored!.RequestHash);
        Assert.Equal(AuthorizationStatus.Pended, stored.Status);
    }

    [Fact]
    public async Task Get_Unknown_Returns404()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.GetAsync($"{Path}/PAS-20261011-NONE0001");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_MalformedNumber_Returns400()
    {
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        var response = await client.GetAsync($"{Path}/PAS_1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_OtherServiceOrUser_Returns403()
    {
        _repo.GetByAuthorizationNumberAsync("PAS-1").Returns(new Authorization { TenantId = Tenant, AuthorizationNumber = "PAS-1" });

        using (var service = ClientWithToken(ServiceToken("claims-service")))
            Assert.Equal(HttpStatusCode.Forbidden, (await service.GetAsync($"{Path}/PAS-1")).StatusCode);
        using (var user = ClientWithToken(_factory.IssueToken(Tenant, ChoRolePermissions.TenantAdmin)))
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"{Path}/PAS-1")).StatusCode);
    }

    // ── Hand-forged tokens: only a real fhir-service service token ─────

    /// <summary>
    /// A token signed with the development key but with claims chosen freely,
    /// for identities no issuer helper would mint.
    /// </summary>
    private static string Forge(string issuer, string? subject, string? authorizedParty, params string[] roles)
    {
        var claims = new Dictionary<string, object>
        {
            [ChoClaimTypes.TenantId] = Tenant,
            [ChoClaimTypes.Role] = roles,
        };
        if (subject is not null) claims[ChoClaimTypes.Subject] = subject;
        if (authorizedParty is not null) claims[ChoClaimTypes.AuthorizedParty] = authorizedParty;

        var now = DateTime.UtcNow;
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(
            new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = ChoDevelopmentAuth.Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                Claims = claims,
                SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                        Convert.FromBase64String(ChoDevelopmentAuth.SymmetricKey)),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            });
    }

    public static TheoryData<string, string, HttpStatusCode> ForgedTokens => new()
    {
        // A user issuer cannot mint a service identity, even with the role and sub = azp.
        { "user issuer, cho.service, sub=azp=fhir-service",
            Forge(ChoDevelopmentAuth.UserIssuer, "fhir-service", "fhir-service", ChoServiceRole.Name), HttpStatusCode.Forbidden },
        // A service issuer's token whose sub and azp disagree names no client.
        { "service issuer, sub=fhir-service, azp=claims-service",
            Forge(ChoDevelopmentAuth.ServiceIssuer, "fhir-service", "claims-service", ChoServiceRole.Name), HttpStatusCode.Forbidden },
        { "service issuer, sub=fhir-service, no azp",
            Forge(ChoDevelopmentAuth.ServiceIssuer, "fhir-service", null, ChoServiceRole.Name), HttpStatusCode.Forbidden },
        // A workload issuer mints workloads (wf-*), never a service: a token
        // from it naming fhir-service is refused at authentication.
        { "workload issuer, cho.service, sub=azp=fhir-service",
            Forge(ChoDevelopmentAuth.WorkloadIssuer, "fhir-service", "fhir-service", ChoServiceRole.Name), HttpStatusCode.Unauthorized },
        { "workload issuer, cho.workload, sub=azp=fhir-service",
            Forge(ChoDevelopmentAuth.WorkloadIssuer, "fhir-service", "fhir-service", ChoWorkloadRole.Name), HttpStatusCode.Unauthorized },
        // An issuer nobody trusts, even signed with a known key, is not authenticated.
        { "unknown issuer",
            Forge("cho-attacker", "fhir-service", "fhir-service", ChoServiceRole.Name), HttpStatusCode.Unauthorized },
    };

    [Theory]
    [MemberData(nameof(ForgedTokens))]
    public async Task ForgedToken_IsRefused(string because, string token, HttpStatusCode expected)
    {
        using var client = ClientWithToken(token);

        var response = await client.PostAsJsonAsync(Path, Body(), Json);

        Assert.True(expected == response.StatusCode, $"{because}: expected {expected}, got {response.StatusCode}");
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task FhirServiceToken_WithAnotherTenantInTheHeader_Returns403()
    {
        // The tenant is the token's; a header naming another is a conflict, not a switch.
        using var client = ClientWithToken(ServiceToken("fhir-service"));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-b");

        var response = await client.PostAsJsonAsync(Path, Body(), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _repo.DidNotReceive().CreateAsync(Arg.Any<Authorization>());
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
