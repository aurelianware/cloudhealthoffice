using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using ClaimsService.Models.Adjudication;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// For a state Medicaid stay, PricingStage sends the member's room and board
/// days on other live inpatient claims, so the adult IMD monthly day limit
/// counts the whole month.
/// </summary>
public class PricingStageImdHistoryTests
{
    private const string Psych = "1730187568";
    private static readonly DateTime Admit = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly IFeeSchedulePricingClient _client = Substitute.For<IFeeSchedulePricingClient>();
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private IReadOnlyList<PricingRequest>? _sent;

    public PricingStageImdHistoryTests()
    {
        _client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.ArgAt<IReadOnlyList<PricingRequest>>(1);
                return new PricingResultSet
                {
                    LineResults = _sent.Select(r => new PricingResult
                    {
                        LineNumber = r.LineNumber, AllowedAmount = 100m, BilledAmount = r.BilledAmount,
                        RateSource = CloudHealthOffice.FeeScheduleEngine.Domain.RateSource.Medicaid,
                        FeeScheduleType = CloudHealthOffice.FeeScheduleEngine.Domain.FeeScheduleType.PerDiem,
                    }).ToList(),
                };
            });
    }

    private PricingStage Sut() => new(_client, NullLogger<PricingStage>.Instance, _claims);

    private static ClaimAdjudicationContext Context(string? method = "texas", string? overLimit = "pend")
    {
        var claim = new AdapterClaim
        {
            TenantId = "tenant-1", Id = "claim-2", ClaimVersionId = "claim-2", MemberId = "MEM-1",
            BillingProviderNPI = Psych, ClaimType = ClaimType.Institutional, ClaimFrequencyCode = "1",
            ServiceDateFrom = Admit, ServiceDateTo = Admit.AddDays(5),
            Institutional = new InstitutionalClaimDetails
            {
                FacilityTypeCode = "11", AdmissionDate = Admit, StatementFromDate = Admit, StatementToDate = Admit.AddDays(5),
                PatientStatusCode = "01",
            },
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, RevenueCode = "0114", ChargeAmount = 6000m, Units = 5m, ServiceDateFrom = Admit },
                new() { LineNumber = 2, RevenueCode = "0250", ChargeAmount = 300m, Units = 3m, ServiceDateFrom = Admit },
            },
        };
        return new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            InpatientGrouping = new InpatientGroupingOutcome("753-2", "753", null, Grouped: true)
            {
                MedicaidDrgMethod = method,
                ImdOverLimitAction = overLimit,
            },
        };
    }

    private static Claim Prior(string id, string npi, DateTime from, decimal days, string rev = "0114",
        ClaimType type = ClaimType.Institutional, ClaimStatus status = ClaimStatus.Paid) => new()
    {
        Id = id, ClaimVersionId = id, TenantId = "tenant-1", MemberId = "MEM-1", BillingProviderNPI = npi,
        ClaimType = type, Status = status, VersionState = ClaimVersionState.Adjudicated,
        ServiceDateFrom = from, ServiceDateTo = from.AddDays((double)days),
        ClaimLines = new List<ClaimLine>
        {
            new() { LineNumber = 1, RevenueCode = rev, Units = days, ServiceDateFrom = from },
            new() { LineNumber = 2, RevenueCode = "0250", Units = 4, ServiceDateFrom = from },
        },
    };

    [Fact]
    public async Task Sends_room_and_board_days_from_the_members_other_live_inpatient_claims()
    {
        _claims.FindDuplicateCandidatesAsync("tenant-1", "MEM-1", new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "claim-2", Arg.Any<CancellationToken>())
            .Returns(new List<Claim>
            {
                Prior("claim-1", Psych, new DateTime(2026, 10, 2), 10),
                Prior("denied", Psych, new DateTime(2026, 10, 2), 10, status: ClaimStatus.Denied),
                Prior("prof", Psych, new DateTime(2026, 10, 2), 10, type: ClaimType.Professional),
                Prior("leave", Psych, new DateTime(2026, 10, 12), 2, rev: "0183"),
            });

        await Sut().ExecuteAsync(Context(), CancellationToken.None);

        var others = Assert.Single(_sent!, r => r.OtherInpatientDays is not null).OtherInpatientDays!;
        var stay = Assert.Single(others);
        Assert.Equal(Psych, stay.FacilityNpi);
        Assert.Equal(new DateTime(2026, 10, 2), stay.From);
        Assert.Equal(10, stay.Days);
        Assert.All(_sent!, r => Assert.Equal("pend", r.ImdOverLimitAction));
    }

    [Fact]
    public async Task A_claim_not_priced_by_a_state_method_does_not_read_history()
    {
        await Sut().ExecuteAsync(Context(method: null), CancellationToken.None);

        await _claims.DidNotReceiveWithAnyArgs().FindDuplicateCandidatesAsync(default!, default!, default, default, default!, default);
        Assert.All(_sent!, r => Assert.Null(r.OtherInpatientDays));
    }

    [Fact]
    public async Task A_failed_history_read_pends_rather_than_counting_half_the_month()
    {
        _claims.FindDuplicateCandidatesAsync(default!, default!, default, default, default!, default)
            .ReturnsForAnyArgs<IReadOnlyList<Claim>>(_ => throw new TimeoutException("mongo"));
        var ctx = Context();

        var result = await Sut().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PricingStage.PricingUnavailablePendCode, ctx.PendDetails!.PendCode);
        Assert.Null(_sent);
    }
}
