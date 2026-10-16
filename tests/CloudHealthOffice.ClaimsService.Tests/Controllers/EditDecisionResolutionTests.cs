using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Workbench step 2b: the reviewer's edit-level decisions on the approval —
/// checked against the pend and the override policy, untouched edits upheld
/// at Finish, the decisions handed to the re-run and recorded on the claim,
/// and override reasons written as Decision notes.
/// </summary>
public class EditDecisionResolutionTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClaimsApiFactory _factory;
    private readonly IClaimRepository _repo;
    private readonly IClaimApprovalReadjudicator _readjudicator;
    private ExaminerApproval? _approval;
    private Claim? _saved;

    public EditDecisionResolutionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _repo = factory.ClaimRepository;
        _readjudicator = factory.ApprovalReadjudicator;
        _readjudicator.ReadjudicateForApprovalAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Do<ExaminerApproval>(a => _approval = a), Arg.Any<CancellationToken>())
            .Returns(new ApprovalReadjudicationResult(ClaimAdjudicationOutcome.Pass, null));
        _repo.HoldsResolutionLockAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _repo.UpdateHoldingResolutionLockAsync(Arg.Do<Claim>(c => _saved = c), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Claim>());
    }

    private HttpClient Client(string role = ChoRolePermissions.ClaimsExaminer, string user = "examiner-1")
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private static NcciEditFailureSnapshot PairEdit(int modifierIndicator) => new()
    {
        EditType = "NcciPair", RuleId = "NE001", Column1Code = "66984", Column2Code = "66982",
        AffectedLineNumbers = [2], ModifierIndicator = modifierIndicator, SuggestedCarc = "B20", SuggestedRarc = "N519",
        Message = "66982 bundled into 66984",
    };

    private static NcciEditFailureSnapshot MueEdit(int mai) => new()
    {
        EditType = "Mue", RuleId = "NE002", Column2Code = "66984", AffectedLineNumbers = [1],
        ServiceDate = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        UnitsBilled = 2, MueMaxUnits = 1, MueAdjudicationIndicator = mai, SuggestedCarc = "151", SuggestedRarc = "N115",
        Message = $"billed 2 units; MUE limit 1 (MAI {mai})",
    };

    private Claim Pended(params NcciEditFailureSnapshot[] edits)
    {
        foreach (var e in edits) e.EditKey = EditIdentity.Key(e);
        var claim = new Claim
        {
            Id = "edit-claim-" + Guid.NewGuid().ToString("N")[..8],
            TenantId = "test-tenant",
            ClaimNumber = "MCC-P-0000695",
            Status = ClaimStatus.Pended,
            VersionState = ClaimVersionState.Submitted,
            BillingProviderNPI = "9262123927",
            LineOfBusiness = LineOfBusiness.Commercial,
            MemberId = "MBR-5304766",
            TotalChargeAmount = 12963m,
            ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            PendDetails = new PendDetails
            {
                PendCode = "MUE", PendReason = edits[0].Message, PendedAt = DateTime.UtcNow, EditFailures = edits.ToList(),
            },
            ClaimLines =
            [
                new() { LineNumber = 1, ProcedureCode = "66984", ChargeAmount = 9000m, Units = 2 },
                new() { LineNumber = 2, ProcedureCode = "66982", ChargeAmount = 3963m, Units = 1 },
            ],
        };
        _repo.GetByIdAsync(claim.Id).Returns(claim);
        return claim;
    }

    private static Task<HttpResponseMessage> Approve(HttpClient client, Claim claim, object? editDecisions, object? lineDenials = null) =>
        client.PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve", new
        {
            disposition = "Approved",
            pendFingerprint = claim.PendDetails!.Fingerprint,
            editDecisions,
            lineDenials,
        }, Json);

    [Fact]
    public async Task Override_with_reason_goes_to_the_rerun_and_untouched_edits_are_upheld_at_Finish()
    {
        var pair = PairEdit(1);
        var mue = MueEdit(1);
        var claim = Pended(mue, pair);

        var response = await Approve(Client(), claim,
            new[] { new { editKey = pair.EditKey, decision = "Override", reason = "Separate incision, modifier missed" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dispositions = _approval!.EditDispositions;
        Assert.Equal(2, dispositions.Count);
        var overridden = Assert.Single(dispositions, d => d.Decision == EditDecision.Override);
        Assert.Equal(pair.EditKey, overridden.EditKey);
        Assert.Equal("Examiner", overridden.PolicyLevel);
        var upheld = Assert.Single(dispositions, d => d.Decision == EditDecision.Uphold);
        Assert.Equal(mue.EditKey, upheld.EditKey);
        Assert.True(upheld.UpheldByDefault);
        Assert.Equal("examiner-1", upheld.DecidedBy);

        var record = Assert.Single(_saved!.ExaminerResolutions);
        Assert.Equal(2, record.EditDispositions.Count);
        Assert.Contains(_factory.NoteRepository.All, n =>
            n.ClaimId == claim.Id && n.Category == ClaimNoteCategories.Decision && n.Text.Contains("Separate incision"));
    }

    [Fact]
    public async Task An_empty_decision_list_upholds_every_edit()
    {
        var claim = Pended(MueEdit(1));

        var response = await Approve(Client(), claim, Array.Empty<object>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var only = Assert.Single(_approval!.EditDispositions);
        Assert.Equal(EditDecision.Uphold, only.Decision);
        Assert.True(only.UpheldByDefault);
    }

    [Fact]
    public async Task Without_edit_decisions_the_approval_is_whole_claim_as_before()
    {
        var claim = Pended(MueEdit(1));

        var response = await Approve(Client(), claim, editDecisions: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_approval!.EditDispositions);
        Assert.Empty(_approval.ManualLineDenials);
    }

    [Fact]
    public async Task Modifier_indicator_0_can_never_be_overridden()
    {
        var pair = PairEdit(0);
        var claim = Pended(pair);

        var response = await Approve(Client(ChoRolePermissions.ClaimsSupervisor), claim,
            new[] { new { editKey = pair.EditKey, decision = "Override", reason = "please" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("can't be overridden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Clinical_MUE_override_needs_a_supervisor()
    {
        var mue = MueEdit(3);
        var claim = Pended(mue);
        var decisions = new[] { new { editKey = mue.EditKey, decision = "Override", reason = "Bilateral, records attached" } };

        var asExaminer = await Approve(Client(), claim, decisions);
        var asSupervisor = await Approve(Client(ChoRolePermissions.ClaimsSupervisor, "supervisor-1"), claim, decisions);

        Assert.Equal(HttpStatusCode.Forbidden, asExaminer.StatusCode);
        Assert.Contains("claims:override-edit", await asExaminer.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, asSupervisor.StatusCode);
        Assert.Equal("Supervisor", Assert.Single(_approval!.EditDispositions).PolicyLevel);
    }

    [Fact]
    public async Task Medicaid_keeps_the_federal_floor_on_MAI_2()
    {
        var mue = MueEdit(2);
        var claim = Pended(mue);
        claim.LineOfBusiness = LineOfBusiness.Medicaid;

        var response = await Approve(Client(ChoRolePermissions.ClaimsSupervisor), claim,
            new[] { new { editKey = mue.EditKey, decision = "Override", reason = "x" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-key", "Override", "r", HttpStatusCode.BadRequest)]
    [InlineData(null, "Maybe", "r", HttpStatusCode.BadRequest)]
    [InlineData(null, "Override", "", HttpStatusCode.BadRequest)]
    public async Task Invalid_decisions_are_refused_before_anything_happens(string? key, string decision, string reason, HttpStatusCode expected)
    {
        var pair = PairEdit(1);
        var claim = Pended(pair);
        _approval = null;

        var response = await Approve(Client(), claim, new[] { new { editKey = key ?? pair.EditKey, decision, reason } });

        Assert.Equal(expected, response.StatusCode);
        Assert.Null(_approval);
    }

    [Fact]
    public async Task Manual_line_denial_needs_a_reviewer_denial_reason_and_goes_to_the_rerun()
    {
        var claim = Pended(MueEdit(1));
        _approval = null;

        var wrongCode = await Approve(Client(), claim, Array.Empty<object>(),
            new[] { new { lineNumber = 2, groupCode = "CO", carc = "45", reason = "x" } });
        var ok = await Approve(Client(), claim, Array.Empty<object>(),
            new[] { new { lineNumber = 2, groupCode = "CO", carc = "50", reason = "Records don't support the second procedure" } });

        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var denial = Assert.Single(_approval!.ManualLineDenials);
        Assert.Equal((2, "CO", "50"), (denial.LineNumber, denial.GroupCode, denial.Carc));
        Assert.Contains(_factory.NoteRepository.All, n => n.ClaimId == claim.Id && n.Text.Contains("Denied line 2 with CO-50"));
    }

    [Fact]
    public async Task Edit_decisions_apply_only_to_an_approval()
    {
        var claim = Pended(MueEdit(1));

        var response = await Client().PostAsJsonAsync($"/api/claims/work-queue/{claim.Id}/resolve",
            new { disposition = "Denied", reason = "no", editDecisions = Array.Empty<object>() }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_edits_endpoint_shows_each_edit_with_its_policy_and_what_the_caller_may_do()
    {
        var claim = Pended(MueEdit(3), PairEdit(1), PairEdit(0));
        claim.PendDetails!.EditFailures[2].Column2Code = "66985";
        claim.PendDetails.EditFailures[2].EditKey = EditIdentity.Key(claim.PendDetails.EditFailures[2]);

        var examiner = await Client().GetFromJsonAsync<JsonElement>($"/api/claims/{claim.Id}/edits", Json);
        var supervisor = await Client(ChoRolePermissions.ClaimsSupervisor).GetFromJsonAsync<JsonElement>($"/api/claims/{claim.Id}/edits", Json);

        static Dictionary<string, (string Level, bool Can)> Read(JsonElement view) => view.GetProperty("edits").EnumerateArray()
            .ToDictionary(e => e.GetProperty("category").GetString()!,
                e => (e.GetProperty("overrideLevel").GetString()!, e.GetProperty("canOverride").GetBoolean()));

        var asExaminer = Read(examiner);
        Assert.Equal(("Supervisor", false), asExaminer[EditOverridePolicy.MueDateClinical]);
        Assert.Equal(("Examiner", true), asExaminer[EditOverridePolicy.NcciModifierAllowed]);
        Assert.Equal(("Never", false), asExaminer[EditOverridePolicy.NcciModifierNotAllowed]);
        Assert.True(Read(supervisor)[EditOverridePolicy.MueDateClinical].Can);
        Assert.Equal(claim.PendDetails.Fingerprint, examiner.GetProperty("pendFingerprint").GetString());
        Assert.Contains(examiner.GetProperty("denialReasons").EnumerateArray(), r => r.GetProperty("carc").GetString() == "50");
    }
}
