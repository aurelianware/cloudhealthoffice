using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanEdits.Contracts;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Approving a claim pended by the plan's own edits (ADR 019), through the
/// resolve endpoint: plan edits stand unless decided one by one, overrides
/// follow the PLAN_EDIT policy, and CHO's own edits keep their behaviour.
/// </summary>
public class PlanEditResolutionTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClaimsApiFactory _factory;
    private readonly IClaimRepository _repo;
    private ExaminerApproval? _approval;

    public PlanEditResolutionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        factory.ApprovalReadjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Do<ExaminerApproval>(a => _approval = a), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pass, null));
        _repo.HoldsResolutionLockAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _repo.UpdateHoldingResolutionLockAsync(Arg.Any<Claim>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Claim>());
    }

    private HttpClient Client()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("examiner-1", ChoRolePermissions.ClaimsExaminer));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private static readonly PlanEditFindingSnapshot Denial = PlanEditsStage.ToSnapshot(new PlanEdit
    {
        RuleId = "PLAN-NC-7", Message = "Not covered", LineNumbers = [1], SuggestedCarc = "96", SuggestedRarc = "N130",
    });

    private static readonly PlanEditFindingSnapshot Review = PlanEditsStage.ToSnapshot(new PlanEdit
    {
        RuleId = "PLAN-HIDOLLAR", Message = "Review", LineNumbers = [2],
    });

    private static readonly NcciEditFailureSnapshot Ncci = new()
    {
        EditType = "NcciPair", RuleId = "NE001", Column1Code = "66984", Column2Code = "66982",
        AffectedLineNumbers = [2], ModifierIndicator = 1, SuggestedCarc = "B20", Message = "bundled",
    };

    private Claim Pended(bool withNcci = false)
    {
        Ncci.EditKey = EditIdentity.Key(Ncci);
        var claim = new Claim
        {
            Id = "plan-edit-claim-" + Guid.NewGuid().ToString("N")[..8],
            TenantId = "test-tenant",
            ClaimNumber = "CLM-PLAN-1",
            Status = ClaimStatus.Pended,
            VersionState = ClaimVersionState.Submitted,
            BillingProviderNPI = "9262123927",
            LineOfBusiness = LineOfBusiness.Commercial,
            MemberId = "MBR-1",
            TotalChargeAmount = 12100m,
            ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            PendDetails = new PendDetails
            {
                PendCode = PlanEditsStage.PlanEditPendCode,
                PendReason = "2 plan edits; first: PLAN-NC-7 Not covered",
                PendedAt = DateTime.UtcNow,
                PlanEditFindings = [Denial, Review],
                EditFailures = withNcci ? [Ncci] : [],
            },
            ClaimLines =
            [
                new() { LineNumber = 1, ProcedureCode = "S9999", ChargeAmount = 100m, Units = 1 },
                new() { LineNumber = 2, ProcedureCode = "J2357", ChargeAmount = 12000m, Units = 1 },
            ],
        };
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        return claim;
    }

    private static Task<HttpResponseMessage> Approve(HttpClient client, Claim claim, object? editDecisions) =>
        client.PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve", new
        {
            disposition = "Approved",
            reason = "Reviewed",
            pendFingerprint = claim.PendDetails!.Fingerprint,
            editDecisions,
        }, Json);

    [Fact]
    public async Task A_whole_claim_approval_upholds_every_plan_edit_by_default()
    {
        var claim = Pended();

        var response = await Approve(Client(), claim, editDecisions: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _approval!.EditDispositions.Count);
        Assert.All(_approval.EditDispositions, d =>
        {
            Assert.Equal(EditDecision.Uphold, d.Decision);
            Assert.True(d.UpheldByDefault);
            Assert.Equal(ClaimEditCatalog.PlanEditType, d.EditType);
        });
    }

    [Fact]
    public async Task A_whole_claim_approval_leaves_CHOs_own_edits_undecided_as_before()
    {
        var claim = Pended(withNcci: true);

        var response = await Approve(Client(), claim, editDecisions: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(_approval!.EditDispositions, d => d.EditKey == Ncci.EditKey);
        Assert.Equal(2, _approval.EditDispositions.Count);
    }

    [Fact]
    public async Task An_examiner_may_override_a_plan_edit_with_a_reason()
    {
        var claim = Pended();

        var response = await Approve(Client(), claim,
            new[] { new { editKey = Denial.EditKey, decision = "Override", reason = "Covered under the rider" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var overridden = Assert.Single(_approval!.EditDispositions, d => d.Decision == EditDecision.Override);
        Assert.Equal(Denial.EditKey, overridden.EditKey);
        Assert.Equal("Examiner", overridden.PolicyLevel);
        Assert.Contains(_approval.EditDispositions, d => d.EditKey == Review.EditKey && d.UpheldByDefault);
    }

    [Fact]
    public async Task Overriding_a_plan_edit_without_a_reason_is_refused()
    {
        var claim = Pended();

        var response = await Approve(Client(), claim,
            new[] { new { editKey = Denial.EditKey, decision = "Override", reason = (string?)null } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_approval);
    }

    [Fact]
    public void An_upheld_by_default_review_edit_changes_no_payment_and_the_denial_edit_denies_its_line()
    {
        var ctx = global::CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.PlanEdits.PlanEditsStageTests.Context(new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = new PendDetails { PendCode = "PLANEDIT", PlanEditFindings = [Denial, Review] },
            EditDispositions =
            [
                new EditDisposition { EditKey = Denial.EditKey!, Decision = EditDecision.Uphold, UpheldByDefault = true },
                new EditDisposition { EditKey = Review.EditKey!, Decision = EditDecision.Uphold, UpheldByDefault = true },
            ],
        });

        var denial = Assert.Single(EditDispositionApplier.LineDenials(ctx));

        Assert.Equal(1, denial.LineNumber);
        Assert.Equal("96", denial.Carc);
    }
}
