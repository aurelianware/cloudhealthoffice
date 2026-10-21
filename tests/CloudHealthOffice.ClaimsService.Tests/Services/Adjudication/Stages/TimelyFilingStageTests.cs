using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// <see cref="TimelyFilingStage"/>: lines are measured from their date of
/// service to the payer receipt date against the most specific configured
/// limit; late claims deny with CARC 29 or pend.
/// </summary>
public class TimelyFilingStageTests
{
    private const string TenantId = "tenant-a";
    private const string BillingNpi = "1234567890";

    private static readonly DateTime Dos = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly IClaimRepository _repository = Substitute.For<IClaimRepository>();

    private TimelyFilingStage NewStage(TimelyFilingOptions? options = null) =>
        new(_repository, Options.Create(options ?? new TimelyFilingOptions { DefaultLimitDays = 90 }),
            NullLogger<TimelyFilingStage>.Instance);

    [Fact]
    public void Defaults_deny_after_365_days_and_run_before_duplicates()
    {
        var options = new TimelyFilingOptions();

        Assert.Equal(TimelyFilingEnforcementMode.Deny, options.Mode);
        Assert.Equal(365, options.DefaultLimitDays);
        Assert.Equal(110, NewStage().Order);
        Assert.InRange(NewStage().Order, 101, 119); // after scrubbing (100), before duplicates (120)
        Assert.False(NewStage().IsRequired);
    }

    [Fact]
    public async Task Claim_received_on_the_last_day_is_timely()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(90).AddHours(23)));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Claim_received_a_day_late_is_denied_with_CARC_29_on_every_line()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(91)));

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal("29", ctx.AdjudicationResult.DenialReasonCode);
        Assert.Contains("90-day filing limit", ctx.AdjudicationResult.DenialReason);
        var header = Assert.Single(ctx.AdjudicationResult.AdjustmentReasons);
        Assert.Equal(("CO", "29", 150m), (header.GroupCode, header.ReasonCode, header.Amount));
        Assert.Equal(2, ctx.LineAdjudicationResults.Count);
        Assert.Equal(100m, ctx.LineAdjudicationResults[0].AdjustmentReasons.Single().Amount);
        Assert.Equal(50m, ctx.LineAdjudicationResults[1].AdjustmentReasons.Single().Amount);
        Assert.All(ctx.LineAdjudicationResults, l => Assert.Equal(0m, l.PaidAmount));
    }

    [Fact]
    public async Task Clock_runs_to_the_receipt_date_not_today()
    {
        // Received in time; adjudicated (re-run, pend, outage) long after.
        var ctx = NewContext(Claim(received: Dos.AddDays(30)));

        var result = await NewStage(new TimelyFilingOptions { DefaultLimitDays = 30 }).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Some_late_lines_pend_naming_them()
    {
        var claim = Claim(received: Dos.AddDays(95));
        claim.ClaimLines[1].ServiceDateFrom = Dos.AddDays(10);
        claim.ClaimLines[1].ServiceDateTo = Dos.AddDays(10);
        var ctx = NewContext(claim);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(TimelyFilingStage.PendCode, ctx.PendDetails!.PendCode);
        Assert.StartsWith("Line(s) 1 received 2026-04-15", ctx.PendDetails.PendReason);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task PendForReview_pends_a_fully_late_claim()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(200)));

        var result = await NewStage(new TimelyFilingOptions
        {
            DefaultLimitDays = 90,
            Mode = TimelyFilingEnforcementMode.PendForReview,
        }).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(TimelyFilingStage.PendCode, ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task SoftValidation_lets_a_late_claim_through()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(200)));

        var result = await NewStage(new TimelyFilingOptions
        {
            DefaultLimitDays = 90,
            Mode = TimelyFilingEnforcementMode.SoftValidation,
        }).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task Examiner_approval_of_exactly_this_pend_overrides_the_limit()
    {
        var options = new TimelyFilingOptions { DefaultLimitDays = 90, Mode = TimelyFilingEnforcementMode.PendForReview };
        var first = NewContext(Claim(received: Dos.AddDays(200)));
        await NewStage(options).ExecuteAsync(first, CancellationToken.None);

        var rerun = NewContext(Claim(received: Dos.AddDays(200)));
        rerun.ExaminerApproval = new ExaminerApproval { ExaminerId = "ex-1", ReviewedPend = first.PendDetails };
        var result = await NewStage(options).ExecuteAsync(rerun, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Contains(rerun.ExaminerOverrides, o => o.StartsWith("TimelyFiling: TIMELY:"));
    }

    [Fact]
    public async Task Approval_of_a_different_pend_does_not_override()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(200)));
        ctx.ExaminerApproval = new ExaminerApproval
        {
            ExaminerId = "ex-1",
            ReviewedPend = new PendDetails { PendCode = "NCCI", PendReason = "something else" },
        };

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
    }

    [Fact]
    public async Task Institutional_claim_is_measured_from_the_statement_through_date()
    {
        var claim = Claim(received: Dos.AddDays(100));
        claim.ClaimType = ClaimType.Institutional;
        claim.ServiceDateFrom = Dos;
        claim.ServiceDateTo = Dos.AddDays(15); // discharge
        var ctx = NewContext(claim);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public void Most_specific_limit_wins()
    {
        var policy = new TimelyFilingPolicy
        {
            DefaultLimitDays = 365,
            LineOfBusinessLimitDays = new() { [LineOfBusiness.Medicaid] = 95, [LineOfBusiness.Commercial] = 180 },
            ProviderRules =
            [
                new() { Name = "Big Hospital contract", BillingProviderNpis = [BillingNpi], LimitDays = 120 },
                new() { Name = "Big Hospital Medicaid", BillingProviderNpis = [BillingNpi], LineOfBusiness = LineOfBusiness.Medicaid, LimitDays = 60 },
            ],
        };

        var medicaid = Claim(received: Dos);
        medicaid.LineOfBusiness = LineOfBusiness.Medicaid;
        Assert.Equal((60, "Big Hospital Medicaid"), TimelyFilingStage.LimitFor(policy, medicaid));

        var commercial = Claim(received: Dos);
        commercial.LineOfBusiness = LineOfBusiness.Commercial;
        Assert.Equal((120, "Big Hospital contract"), TimelyFilingStage.LimitFor(policy, commercial));

        var otherProvider = Claim(received: Dos);
        otherProvider.BillingProviderNPI = "9999999999";
        otherProvider.LineOfBusiness = LineOfBusiness.Commercial;
        Assert.Equal((180, (string?)null), TimelyFilingStage.LimitFor(policy, otherProvider));

        otherProvider.LineOfBusiness = LineOfBusiness.Medicare;
        Assert.Equal((365, (string?)null), TimelyFilingStage.LimitFor(policy, otherProvider));
    }

    [Fact]
    public async Task Tenant_policy_replaces_the_default()
    {
        var options = new TimelyFilingOptions
        {
            DefaultLimitDays = 365,
            Tenants = { [TenantId] = new TimelyFilingPolicy { DefaultLimitDays = 30 } },
        };
        var ctx = NewContext(Claim(received: Dos.AddDays(60)));

        var result = await NewStage(options).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Contains("30-day", ctx.AdjudicationResult.DenialReason);
    }

    [Fact]
    public void Options_bind_from_configuration()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Adjudication:TimelyFiling:Mode"] = "PendForReview",
                ["Adjudication:TimelyFiling:DefaultLimitDays"] = "180",
                ["Adjudication:TimelyFiling:LineOfBusinessLimitDays:Medicaid"] = "95",
                ["Adjudication:TimelyFiling:ProviderRules:0:Name"] = "Contract A",
                ["Adjudication:TimelyFiling:ProviderRules:0:BillingProviderNpis:0"] = BillingNpi,
                ["Adjudication:TimelyFiling:ProviderRules:0:LimitDays"] = "120",
                ["Adjudication:TimelyFiling:Tenants:tenant-b:DefaultLimitDays"] = "30",
            })
            .Build();

        var options = new TimelyFilingOptions();
        config.GetSection(TimelyFilingOptions.SectionName).Bind(options);

        Assert.Equal(TimelyFilingEnforcementMode.PendForReview, options.Mode);
        Assert.Equal(180, options.DefaultLimitDays);
        Assert.Equal(95, options.LineOfBusinessLimitDays[LineOfBusiness.Medicaid]);
        Assert.Equal(120, Assert.Single(options.ProviderRules).LimitDays);
        Assert.Equal(30, options.For("tenant-b").DefaultLimitDays);
        Assert.Same(options, options.For("tenant-a"));
    }

    [Fact]
    public async Task No_configured_limit_passes()
    {
        var ctx = NewContext(Claim(received: Dos.AddDays(5000)));

        var result = await NewStage(new TimelyFilingOptions { DefaultLimitDays = null }).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Claim_without_a_receipt_date_pends()
    {
        var claim = Claim(received: Dos);
        claim.ReceivedDate = null;
        claim.SubmittedDate = default;
        var ctx = NewContext(claim);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(TimelyFilingStage.NoReceiptDateReason, ctx.PendDetails!.PendReason);
    }

    [Fact]
    public async Task Correction_is_measured_from_the_first_versions_receipt()
    {
        // The original was received in time; its replacement arrives a year later.
        _repository.GetByIdAsync("v2").Returns(Version("v2", predecessor: "v1", received: Dos.AddDays(200)));
        _repository.GetByIdAsync("v1").Returns(Version("v1", predecessor: null, received: Dos.AddDays(20)));
        var correction = Claim(received: Dos.AddDays(400));
        correction.PredecessorVersionId = "v2";
        var ctx = NewContext(correction);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Correcting_a_late_claim_does_not_make_it_timely()
    {
        _repository.GetByIdAsync("v1").Returns(Version("v1", predecessor: null, received: Dos.AddDays(120)));
        var correction = Claim(received: Dos.AddDays(130));
        correction.PredecessorVersionId = "v1";
        var ctx = NewContext(correction);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
    }

    [Fact]
    public async Task Correction_whose_original_cannot_be_read_pends_as_unavailable()
    {
        _repository.GetByIdAsync("v1").ThrowsAsync(new TimeoutException("mongo"));
        var correction = Claim(received: Dos.AddDays(10));
        correction.PredecessorVersionId = "v1";
        var ctx = NewContext(correction);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.CheckUnavailable);
        Assert.Equal(TimelyFilingStage.OriginalUnavailableReason, ctx.PendDetails!.PendReason);
    }

    [Fact]
    public async Task Original_in_another_tenant_is_not_used()
    {
        var foreign = Version("v1", predecessor: null, received: Dos.AddDays(10));
        foreign.TenantId = "tenant-b";
        _repository.GetByIdAsync("v1").Returns(foreign);
        var correction = Claim(received: Dos.AddDays(10));
        correction.PredecessorVersionId = "v1";
        var ctx = NewContext(correction);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.CheckUnavailable);
    }

    private static ClaimAdjudicationContext NewContext(AdapterClaim claim) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = claim.ClaimVersionId,
        Claim = claim,
    };

    private static Claim Version(string id, string? predecessor, DateTime received) => new()
    {
        TenantId = TenantId,
        Id = id,
        ClaimVersionId = "chain-1",
        PredecessorVersionId = predecessor,
        SubmittedDate = received,
        ReceivedDate = received,
    };

    private static AdapterClaim Claim(DateTime received) => new()
    {
        TenantId = TenantId,
        Id = "claim-1",
        ClaimVersionId = "chain-1",
        ClaimNumber = "CN-1",
        MemberId = "M-1",
        BillingProviderNPI = BillingNpi,
        LineOfBusiness = LineOfBusiness.Commercial,
        ClaimType = ClaimType.Professional,
        TotalChargeAmount = 150m,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        SubmittedDate = received,
        ReceivedDate = received,
        ClaimLines =
        [
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, ServiceDateFrom = Dos, ServiceDateTo = Dos },
            new() { LineNumber = 2, ProcedureCode = "36415", ChargeAmount = 50m, ServiceDateFrom = Dos, ServiceDateTo = Dos },
        ],
    };
}
