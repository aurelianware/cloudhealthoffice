using ClaimsService.Models;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.PlanEdits;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.PlanEdits.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.PlanEdits;

/// <summary>
/// The plan's own edits in the adjudication pipeline (ADR 019): what the
/// stage sends, how it pends, how an examiner's decisions settle the edits
/// on the approval re-run, and what an unusable answer does to the claim.
/// </summary>
public class PlanEditsStageTests
{
    private const string Tenant = "tenant-1";
    private static readonly DateTime Dos = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private static readonly PlanEdit ReviewEdit = new() { RuleId = "PLAN-HIDOLLAR", Message = "Line over $10,000 needs review", LineNumbers = [2] };
    private static readonly PlanEdit DenialEdit = new() { RuleId = "PLAN-NC-7", Message = "Not a covered service under this plan", LineNumbers = [1], SuggestedCarc = "96", SuggestedRarc = "N130" };

    // ── Stage contract ────────────────────────────────────────────────────

    [Fact]
    public void Runs_after_NCCI_and_before_the_AI_examination_and_is_optional()
    {
        var stage = new PlanEditsStage(new FakeClient(), NullLogger<PlanEditsStage>.Instance);

        Assert.Equal("PlanEdits", stage.Name);
        Assert.Equal(450, stage.Order);
        Assert.False(stage.IsRequired);
    }

    [Fact]
    public async Task A_tenant_without_a_plan_edit_service_passes_and_nothing_is_sent()
    {
        var client = new FakeClient(configured: false);

        var result = await Stage(client).ExecuteAsync(Context(), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task No_edits_fired_passes()
    {
        var ctx = Context();

        var result = await Stage(new FakeClient()).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
    }

    // ── What is sent ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_request_carries_claim_facts_and_member_age_and_sex_but_no_names_or_notes()
    {
        var client = new FakeClient();
        var ctx = Context();
        ctx.Claim.PatientFirstName = "Jane";
        ctx.Claim.PatientLastName = "Doe";
        ctx.Claim.ClaimNotes = "free text the plan must not receive";
        ctx.ResolvedMember = new ResolvedMember { MemberId = "M1", DateOfBirth = new DateTime(1980, 2, 3), Gender = "F" };

        await Stage(client).ExecuteAsync(ctx, CancellationToken.None);

        var (tenant, request, lines) = Assert.Single(client.Requests);
        Assert.Equal(Tenant, tenant);
        Assert.Equal(PlanEditContract.Version, request.ContractVersion);
        Assert.Equal(PlanEditPurpose.Adjudication, request.Purpose);
        Assert.False(string.IsNullOrEmpty(request.RequestId));
        Assert.Equal("v1", request.Claim.ClaimVersionId);
        Assert.Equal("Professional", request.Claim.ClaimType);
        Assert.Equal("2026-09-16", request.Claim.ServiceDateFrom);
        Assert.Equal("1980-02-03", request.Claim.Member!.BirthDate);
        Assert.Equal("F", request.Claim.Member.Gender);
        Assert.Equal(["99213", "99214"], request.Claim.Lines.Select(l => l.ProcedureCode));
        Assert.Equal([1, 2], lines.Order());

        var json = System.Text.Json.JsonSerializer.Serialize(request, PlanEditJson.Options);
        Assert.DoesNotContain("Jane", json);
        Assert.DoesNotContain("Doe", json);
        Assert.DoesNotContain("free text", json);
    }

    [Fact]
    public async Task Each_run_gets_its_own_request_id()
    {
        var client = new FakeClient();

        await Stage(client).ExecuteAsync(Context(), CancellationToken.None);
        await Stage(client).ExecuteAsync(Context(), CancellationToken.None);

        Assert.NotEqual(client.Requests[0].Request.RequestId, client.Requests[1].Request.RequestId);
    }

    // ── Edits fired ───────────────────────────────────────────────────────

    [Fact]
    public async Task Fired_edits_pend_the_claim_PLANEDIT_with_the_edits_on_the_pend()
    {
        var ctx = Context();

        var result = await Stage(new FakeClient(ReviewEdit, DenialEdit)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.False(result.CheckUnavailable);
        Assert.Equal("PLANEDIT", ctx.PendDetails!.PendCode);
        Assert.Equal("2 plan edits; first: PLAN-HIDOLLAR Line over $10,000 needs review", ctx.PendDetails.PendReason);
        Assert.Equal(2, ctx.PendDetails.PlanEditFindings.Count);
        Assert.Equal(2, ctx.PlanEditFindings.Count);

        var denial = ctx.PendDetails.PlanEditFindings.Single(e => e.RuleId == "PLAN-NC-7");
        Assert.Equal("PLAN|PLAN-NC-7|1", denial.EditKey);
        Assert.True(denial.IsDenialEdit);
        Assert.Equal("96", denial.SuggestedCarc);
        Assert.False(ctx.PendDetails.PlanEditFindings.Single(e => e.RuleId == "PLAN-HIDOLLAR").IsDenialEdit);
    }

    [Fact]
    public async Task The_same_edit_returned_twice_is_recorded_once()
    {
        var ctx = Context();
        var again = ReviewEdit with { Message = "reworded" };

        await Stage(new FakeClient(ReviewEdit, again)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.PendDetails!.PlanEditFindings);
        Assert.StartsWith("Plan edit PLAN-HIDOLLAR:", ctx.PendDetails.PendReason);
    }

    [Fact]
    public async Task An_earlier_pend_keeps_its_code_and_the_plan_edits_are_added()
    {
        var ctx = Context();
        ctx.PendDetails = new PendDetails { PendCode = "NCCI", PendReason = "NCCI pair", PendedAt = Dos };

        var result = await Stage(new FakeClient(ReviewEdit)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal("NCCI", ctx.PendDetails.PendCode);
        var added = Assert.Single(ctx.PendDetails.AdditionalPendReasons);
        Assert.Equal("PLANEDIT: Plan edit PLAN-HIDOLLAR: Line over $10,000 needs review", added);
        Assert.Single(ctx.PendDetails.PlanEditFindings);
    }

    [Fact]
    public async Task Soft_validation_passes_and_names_the_edits_without_pending()
    {
        var ctx = Context();
        var client = new FakeClient(ReviewEdit) { Mode = PlanEditEnforcementMode.SoftValidation };

        var result = await Stage(client).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Empty(ctx.PlanEditFindings);
        Assert.Contains(result.Notes, n => n.Contains("PLAN-HIDOLLAR") && n.Contains("soft validation"));
    }

    [Fact]
    public async Task A_long_plan_message_is_cut_to_the_pend_reason_limit()
    {
        var ctx = Context();
        var edit = ReviewEdit with { Message = new string('x', 500) };

        await Stage(new FakeClient(edit)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(500, ctx.PendDetails!.PendReason!.Length);
    }

    // ── No usable answer ──────────────────────────────────────────────────

    [Theory]
    [InlineData(PlanEditUnavailableKinds.Timeout)]
    [InlineData(PlanEditUnavailableKinds.Unreachable)]
    [InlineData(PlanEditUnavailableKinds.InvalidResponse)]
    public async Task No_usable_answer_pends_SVCUNAVAIL_as_a_check_that_could_not_run(string kind)
    {
        var ctx = Context();
        var client = new FakeClient { Failure = new PlanEditCallResult.Unavailable(kind, "detail") };

        var result = await Stage(client).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.CheckUnavailable);
        Assert.Equal(ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Contains($"plan edit service {kind}", ctx.PendDetails.PendReason);
        Assert.True(ctx.IsCheckUnavailable(ctx.PendDetails.PendReason));
        Assert.True(ClaimAdjudicationOrchestrator.AnyCheckFailed(ctx));
        Assert.Empty(ctx.PlanEditFindings);
    }

    [Fact]
    public async Task No_usable_answer_passes_with_a_note_when_the_plan_chose_Pass()
    {
        var ctx = Context();
        var client = new FakeClient
        {
            WhenUnavailable = PlanEditUnavailableMode.Pass,
            Failure = new PlanEditCallResult.Unavailable(PlanEditUnavailableKinds.Timeout, "detail"),
        };

        var result = await Stage(client).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Contains(result.Notes, n => n.StartsWith("Plan edits skipped") && n.Contains("timeout"));
    }

    // ── Approval re-run ───────────────────────────────────────────────────

    [Fact]
    public async Task On_the_approval_re_run_decided_edits_are_settled_and_the_stage_passes()
    {
        var reviewed = Snapshot(ReviewEdit);
        var denial = Snapshot(DenialEdit);
        var ctx = Context(Approval(
            Disposition(reviewed.EditKey!, EditDecision.Override, "Reviewed the operative note"),
            Disposition(denial.EditKey!, EditDecision.Uphold)));
        var client = new FakeClient(ReviewEdit, DenialEdit);

        var result = await Stage(client).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(PlanEditPurpose.ApprovalReadjudication, client.Requests[0].Request.Purpose);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Contains(result.Notes, n => n.Contains("overridden by examiner") && n.Contains("operative note"));
        Assert.Contains(result.Notes, n => n.Contains("PLAN-NC-7 upheld by examiner"));
        Assert.Equal(2, ctx.ExaminerOverrides.Count);
    }

    [Fact]
    public async Task On_the_approval_re_run_an_edit_nobody_decided_still_pends()
    {
        var ctx = Context(Approval(Disposition(Snapshot(ReviewEdit).EditKey!, EditDecision.Override, "ok")));

        var result = await Stage(new FakeClient(ReviewEdit, DenialEdit)).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        var pended = Assert.Single(ctx.PendDetails!.PlanEditFindings);
        Assert.Equal("PLAN-NC-7", pended.RuleId);
        Assert.Contains(result.Notes, n => n.Contains("overridden"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    internal static PlanEditFindingSnapshot Snapshot(PlanEdit edit) => PlanEditsStage.ToSnapshot(edit);

    private static PlanEditsStage Stage(FakeClient client) => new(client, NullLogger<PlanEditsStage>.Instance);

    private static EditDisposition Disposition(string key, EditDecision decision, string? reason = null) => new()
    {
        EditKey = key,
        Decision = decision,
        Reason = reason,
        DecidedBy = "examiner-1",
        DecidedAt = Dos,
    };

    private static ExaminerApproval Approval(params EditDisposition[] dispositions) => new()
    {
        ExaminerId = "examiner-1",
        EditDispositions = dispositions,
    };

    internal static ClaimAdjudicationContext Context(ExaminerApproval? approval = null) => new()
    {
        TenantId = Tenant,
        ClaimVersionId = "v1",
        ExaminerApproval = approval,
        Claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "v1",
            ClaimVersionId = "v1",
            ClaimNumber = "CLM-1",
            MemberId = "M1",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = Dos,
            ServiceDateTo = Dos,
            ClaimLines =
            [
                new() { LineNumber = 1, ProcedureCode = "99213", Units = 1, ChargeAmount = 100m, ServiceDateFrom = Dos, ServiceDateTo = Dos, DiagnosisPointers = [1] },
                new() { LineNumber = 2, ProcedureCode = "99214", Units = 1, ChargeAmount = 12000m, ServiceDateFrom = Dos, ServiceDateTo = Dos, DiagnosisPointers = [1] },
            ],
            DiagnosisCodes = [new() { Code = "H25.11", PointerNumber = 1 }],
        },
    };

    private sealed class FakeClient(params PlanEdit[] edits) : IPlanEditServiceClient
    {
        private readonly bool _configured = true;

        public FakeClient(bool configured) : this() => _configured = configured;

        public PlanEditEnforcementMode Mode { get; init; } = PlanEditEnforcementMode.PendForReview;
        public PlanEditUnavailableMode WhenUnavailable { get; init; } = PlanEditUnavailableMode.Pend;
        public PlanEditCallResult.Unavailable? Failure { get; init; }
        public List<(string Tenant, PlanEditRequest Request, IReadOnlyCollection<int> Lines)> Requests { get; } = [];

        public PlanEditTenantOptions? For(string tenantId) => _configured
            ? new PlanEditTenantOptions { Endpoint = "https://edits.plan.example/v1", Mode = Mode, WhenUnavailable = WhenUnavailable }
            : null;

        public Task<PlanEditCallResult> EvaluateAsync(
            string tenantId, PlanEditRequest request, IReadOnlyCollection<int> claimLineNumbers, CancellationToken ct)
        {
            Requests.Add((tenantId, request, claimLineNumbers));
            return Task.FromResult<PlanEditCallResult>(Failure is null ? new PlanEditCallResult.Answered(edits) : Failure);
        }
    }
}
