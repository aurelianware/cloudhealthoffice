using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.CodeValidity;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// <see cref="CodeValidityStage"/>: ICD-10-CM/PCS diagnoses and procedures and
/// HCPCS Level II line codes checked against the date of service. Report-only by
/// default; unknown answers and outages are never findings.
/// </summary>
public class CodeValidityStageTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTime Dos = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ICodeValidityClient _client = Substitute.For<ICodeValidityClient>();
    private IReadOnlyList<CodeValidityQuery>? _sent;

    private CodeValidityStage NewStage(CodeValidityEnforcementMode mode = CodeValidityEnforcementMode.Report) =>
        new(_client, Options.Create(new CodeValidityOptions { Mode = mode }), NullLogger<CodeValidityStage>.Instance);

    /// <summary>Answers each query with the status the map gives its code (valid when absent).</summary>
    private void Answer(Dictionary<string, (string Status, string? Reason)> byCode) =>
        _client.ValidateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeValidityQuery>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var queries = call.ArgAt<IReadOnlyList<CodeValidityQuery>>(1);
                _sent = queries;
                IReadOnlyList<CodeValidityAnswer> answers = queries.Select(q =>
                {
                    var (status, reason) = byCode.TryGetValue(q.Code, out var a) ? a : ("valid", null);
                    return new CodeValidityAnswer
                    {
                        Status = status, Reason = reason, Code = q.Code, System = q.System,
                        Message = $"{q.Code} {reason}", ReleaseVersion = "FY2026",
                    };
                }).ToList();
                return Task.FromResult<IReadOnlyList<CodeValidityAnswer>?>(answers);
            });

    [Fact]
    public void Defaults_report_only_and_run_right_after_scrubbing()
    {
        Assert.Equal(CodeValidityEnforcementMode.Report, new CodeValidityOptions().Mode);
        Assert.InRange(NewStage().Order, 101, 109); // after scrubbing (100), before timely filing (110)
        Assert.False(NewStage().IsRequired);
        Assert.Contains(CodeValidityStage.StageName, ClaimAdjudicationOrchestrator.ShadowSafeStages);
    }

    [Fact]
    public async Task Checks_icd10_diagnoses_and_hcpcs_lines_but_not_cpt_or_cdt()
    {
        Answer([]);
        var claim = Claim(dx: ["E11.65", "Z23"], lines: ["99213", "J1234", "D1110", "A0425"]);

        var result = await NewStage().ExecuteAsync(NewContext(claim), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(["E1165", "Z23", "J1234", "A0425"], _sent!.Select(q => q.Code).ToArray());
        Assert.Equal(CodeValidityStage.Icd10CmSystem, _sent![0].System);
        Assert.Equal(CodeValidityStage.HcpcsSystem, _sent![2].System);
        Assert.All(_sent!, q => Assert.Equal(DateOnly.FromDateTime(Dos), q.Date));
    }

    [Fact]
    public async Task Report_mode_records_findings_and_passes()
    {
        Answer(new() { ["E11"] = ("invalid", "not-billable"), ["J9999"] = ("invalid", "terminated") });
        var ctx = NewContext(Claim(dx: ["E11"], lines: ["J9999"]));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(2, result.Notes.Count);
        Assert.Equal(["Diagnosis ICD-10-CM E11: not-billable", "Line 1 HCPCS J9999: terminated"],
            ctx.CodeValidityFindings.Select(f => f.Label()).ToArray());
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Unknown_answers_are_not_findings()
    {
        Answer(new() { ["E1165"] = ("unknown", "no-release-for-date") });
        var ctx = NewContext(Claim(dx: ["E11.65"], lines: []));

        var result = await NewStage(CodeValidityEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Empty(ctx.CodeValidityFindings);
    }

    [Fact]
    public async Task Outage_passes_unchecked_with_a_note_even_in_deny_mode()
    {
        _client.ValidateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<CodeValidityQuery>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CodeValidityAnswer>?>(null));
        var ctx = NewContext(Claim(dx: ["E11.65"], lines: []));

        var result = await NewStage(CodeValidityEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Contains("not checked", Assert.Single(result.Notes));
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Deny_mode_uses_CARC_146_for_a_diagnosis_on_header_and_lines()
    {
        Answer(new() { ["E11"] = ("invalid", "not-billable"), ["J9999"] = ("invalid", "terminated") });
        var ctx = NewContext(Claim(dx: ["E11"], lines: ["J9999"]));

        var result = await NewStage(CodeValidityEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal("146", ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal(("CO", "146"), (ctx.AdjudicationResult.AdjustmentReasons.Single().GroupCode,
            ctx.AdjudicationResult.AdjustmentReasons.Single().ReasonCode));
        Assert.All(ctx.LineAdjudicationResults, l => Assert.Equal(0m, l.PaidAmount));
    }

    [Fact]
    public async Task Deny_mode_uses_CARC_181_when_only_a_procedure_is_invalid()
    {
        Answer(new() { ["J9999"] = ("invalid", "terminated") });
        var ctx = NewContext(Claim(dx: ["E11.65"], lines: ["J9999"]));

        await NewStage(CodeValidityEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("181", ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Pend_mode_pends_CODEVALID()
    {
        Answer(new() { ["J9999"] = ("invalid", "terminated") });
        var ctx = NewContext(Claim(dx: [], lines: ["J9999"]));

        var result = await NewStage(CodeValidityEnforcementMode.PendForReview).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CodeValidityStage.PendCode, ctx.PendDetails?.PendCode);
    }

    [Fact]
    public async Task Off_mode_does_not_call_terminology()
    {
        var result = await NewStage(CodeValidityEnforcementMode.Off).ExecuteAsync(NewContext(Claim(dx: ["E11"], lines: [])), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _client.DidNotReceiveWithAnyArgs().ValidateAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("ABK", CodeValidityStage.Icd10CmSystem)]
    [InlineData("abf", CodeValidityStage.Icd10CmSystem)]
    [InlineData("", CodeValidityStage.Icd10CmSystem)]
    [InlineData("BBR", CodeValidityStage.Icd10PcsSystem)]
    [InlineData("BK", null)]
    public void Qualifier_names_the_system(string qualifier, string? expected)
        => Assert.Equal(expected, CodeValidityStage.System(qualifier));

    private static ClaimAdjudicationContext NewContext(AdapterClaim claim) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = claim.ClaimVersionId,
        Claim = claim,
    };

    private static AdapterClaim Claim(string[] dx, string[] lines) => new()
    {
        TenantId = TenantId,
        Id = "claim-1",
        ClaimVersionId = "chain-1",
        ClaimNumber = "CN-1",
        MemberId = "M-1",
        BillingProviderNPI = "1234567890",
        ClaimType = ClaimType.Professional,
        TotalChargeAmount = 100m * lines.Length,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        DiagnosisCodes = dx.Select((c, i) => new AdapterDiagnosisCode
        {
            Code = c, CodeQualifier = i == 0 ? "ABK" : "ABF", PointerNumber = i + 1,
        }).ToList(),
        ClaimLines = lines.Select((c, i) => new AdapterClaimLine
        {
            LineNumber = i + 1, ProcedureCode = c, ChargeAmount = 100m, ServiceDateFrom = Dos, ServiceDateTo = Dos,
        }).ToList(),
    };
}
