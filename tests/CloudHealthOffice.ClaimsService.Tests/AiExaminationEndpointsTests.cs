using System.Net;
using System.Net.Http.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Endpoint-level tests for the AI Claims Examiner integration on claims-service:
///   PUT  /api/claims/{id}/ai-examination
///   POST /api/claims/{id}/ai-examination/agreement
///   GET  /api/claims/{id}/ai-examination/audit
///
/// These tests pin the controller-side contract that claims-examiner-service
/// depends on. They mock the audit repository so we exercise the routing,
/// validation, and audit-append wiring without touching real Mongo/Cosmos.
/// </summary>
public class AiExaminationEndpointsTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;

    public AiExaminationEndpointsTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        // Reset recorded calls between tests. The factory is a class fixture
        // (singleton per test class) so the substitutes carry state across tests;
        // clearing calls keeps per-test Received() assertions honest.
        factory.ClaimRepository.ClearReceivedCalls();
        factory.AuditRepository.ClearReceivedCalls();
    }

    private static Claim PendedClaim(string id = "claim-1") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        ClaimNumber = "CLM-1",
        MemberId = "MEM-1",
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        ClaimType = ClaimType.Professional,
        Status = ClaimStatus.Pended,
        ServiceDateFrom = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 1500m,
        PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures = new()
            {
                new()
                {
                    EditType = "NcciPair", RuleId = "NE001",
                    Column1Code = "27447", Column2Code = "27486",
                    AffectedLineNumbers = new() { 2 }
                }
            }
        }
    };

    [Fact]
    public async Task SetAiExamination_On_Pended_Claim_Persists_And_Appends_Audit()
    {
        var claim = PendedClaim();
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);
        _factory.ClaimRepository.UpdateAsync(Arg.Any<Claim>()).Returns(c => c.Arg<Claim>());

        var dto = new AiExamination
        {
            RecommendedDisposition = "RequestInfo",
            ConfidenceScore = 0.78,
            Rationale = "Modifier -59 absent on line 2; identical diagnosis pointers.",
            PolicyCitations = new() { "NCCI Policy Manual Ch.1 §F.3" },
            ModelId = "claude-opus-4-6",
            PromptVersion = "ncci-pend-v1"
        };

        var resp = await _client.PutAsJsonAsync($"/api/claims/{claim.Id}/ai-examination", dto);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Audit row was appended with snapshotted pend context (rule id + code pair).
        await _factory.AuditRepository.Received(1).AppendAsync(
            Arg.Is<AiExaminationAudit>(a =>
                a.ClaimId == claim.Id &&
                a.TenantId == "test-tenant" &&
                a.RuleId == "NE001" &&
                a.Column1Code == "27447" &&
                a.Column2Code == "27486" &&
                a.RecommendedDisposition == "RequestInfo" &&
                a.PromptVersion == "ncci-pend-v1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAiExamination_Returns_409_When_Claim_Not_Pended()
    {
        var claim = PendedClaim();
        claim.Status = ClaimStatus.Approved;
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);

        var dto = new AiExamination { RecommendedDisposition = "Approve", ConfidenceScore = 0.9 };
        var resp = await _client.PutAsJsonAsync($"/api/claims/{claim.Id}/ai-examination", dto);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        // No audit row written when claim was not in Pended status.
        await _factory.AuditRepository.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    [Fact]
    public async Task SetAiExamination_Rejects_Invalid_Disposition()
    {
        var claim = PendedClaim();
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);

        var dto = new AiExamination { RecommendedDisposition = "Maybe", ConfidenceScore = 0.5 };
        var resp = await _client.PutAsJsonAsync($"/api/claims/{claim.Id}/ai-examination", dto);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        await _factory.AuditRepository.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    [Fact]
    public async Task SetAiExamination_Preserves_Prior_Examiner_Agreement_On_Reexamination()
    {
        var claim = PendedClaim();
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "Approve",
            ConfidenceScore = 0.6,
            ExaminerAgreement = "Modified",
            ExaminerActedAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            ExaminerUserId = "examiner-1"
        };
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);
        Claim? saved = null;
        _factory.ClaimRepository.UpdateAsync(Arg.Do<Claim>(c => saved = c))
            .Returns(c => c.Arg<Claim>());

        var dto = new AiExamination { RecommendedDisposition = "Deny", ConfidenceScore = 0.92 };
        var resp = await _client.PutAsJsonAsync($"/api/claims/{claim.Id}/ai-examination", dto);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(saved);
        // Re-examination overwrote disposition but kept the human-feedback fields.
        Assert.Equal("Deny", saved!.AiExamination!.RecommendedDisposition);
        Assert.Equal("Modified", saved.AiExamination.ExaminerAgreement);
        Assert.Equal("examiner-1", saved.AiExamination.ExaminerUserId);
    }

    [Fact]
    public async Task SetExaminerAgreement_Cascades_To_Audit_Repository()
    {
        var claim = PendedClaim();
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "Approve",
            ConfidenceScore = 0.9
        };
        _factory.ClaimRepository.GetByIdAsync(claim.Id).Returns(claim);
        _factory.ClaimRepository.UpdateAsync(Arg.Any<Claim>()).Returns(c => c.Arg<Claim>());
        _factory.AuditRepository.SetExaminerAgreementAsync(
                claim.Id, "test-tenant", "Overridden", "examiner-1", "wrong call",
                Arg.Any<CancellationToken>())
            .Returns(new AiExaminationAudit
            {
                ClaimId = claim.Id, TenantId = "test-tenant",
                RecommendedDisposition = "Approve",
                ExaminerAgreement = "Overridden", ExaminerUserId = "examiner-1"
            });

        var body = new
        {
            agreement = "Overridden",
            examinerUserId = "examiner-1",
            notes = "wrong call"
        };
        var resp = await _client.PostAsJsonAsync(
            $"/api/claims/{claim.Id}/ai-examination/agreement", body);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await _factory.AuditRepository.Received(1).SetExaminerAgreementAsync(
            claim.Id, "test-tenant", "Overridden", "examiner-1", "wrong call",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetExaminerAgreement_Rejects_Invalid_Agreement_Value()
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/claims/claim-1/ai-examination/agreement",
            new { agreement = "Maybe", examinerUserId = "examiner-1" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        await _factory.AuditRepository.DidNotReceiveWithAnyArgs()
            .SetExaminerAgreementAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task GetAiExaminationAudit_Returns_History_Newest_First()
    {
        var history = new List<AiExaminationAudit>
        {
            new()
            {
                Id = "a2", ClaimId = "claim-1", TenantId = "test-tenant",
                RecommendedDisposition = "Deny", ConfidenceScore = 0.92,
                PromptVersion = "ncci-pend-v1",
                GeneratedAt = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
            },
            new()
            {
                Id = "a1", ClaimId = "claim-1", TenantId = "test-tenant",
                RecommendedDisposition = "RequestInfo", ConfidenceScore = 0.7,
                PromptVersion = "ncci-pend-v1",
                GeneratedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
            }
        };
        _factory.AuditRepository.GetByClaimAsync("claim-1", "test-tenant", Arg.Any<CancellationToken>())
            .Returns(history);

        var resp = await _client.GetAsync("/api/claims/claim-1/ai-examination/audit");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<List<AiExaminationAudit>>();
        Assert.NotNull(body);
        Assert.Equal(2, body!.Count);
        Assert.Equal("a2", body[0].Id);
        Assert.Equal("Deny", body[0].RecommendedDisposition);
    }
}
