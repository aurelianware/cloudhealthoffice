using System.Net.Http;
using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// The payer receipt date (<see cref="Claim.SubmittedDate"/> /
/// <see cref="Claim.ReceivedDate"/>) is stamped by
/// <see cref="ClaimSubmissionService"/> from its injected clock at intake and
/// never taken from the submitter: timely filing, prompt-pay clocks, aging and
/// the dashboard windows are measured from it.
/// </summary>
public class ClaimReceiptDateStampingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 30, 0, TimeSpan.Zero);

    private readonly IClaimAdapter _adapter = Substitute.For<IClaimAdapter>();
    private readonly ManualClock _clock = new(Now);
    private readonly ClaimSubmissionService _sut;
    private readonly List<AdapterClaim> _persisted = new();

    public ClaimReceiptDateStampingTests()
    {
        _adapter.Platform.Returns("cho");
        _adapter
            .SubmitClaimAsync(Arg.Any<ClaimSubmissionAdapterRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var claim = ci.Arg<ClaimSubmissionAdapterRequest>().Claim;
                // Snapshot what reached the adapter (i.e. what would be persisted).
                _persisted.Add(new AdapterClaim
                {
                    SubmittedDate = claim.SubmittedDate,
                    ReceivedDate = claim.ReceivedDate,
                    SubmitterTransactionDate = claim.SubmitterTransactionDate,
                });
                claim.Id = $"claim-{_persisted.Count}";
                return new ClaimAdapterResponse { Platform = "cho", Claim = claim };
            });

        var cache = new ClaimTenantConfigCache(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IConfiguration>(),
            NullLogger<ClaimTenantConfigCache>.Instance);
        var factory = new ClaimAdapterFactory(new[] { _adapter }, cache, NullLogger<ClaimAdapterFactory>.Instance);

        _sut = new ClaimSubmissionService(
            factory,
            Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<IMessageBus>(),
            NullLogger<ClaimSubmissionService>.Instance,
            _clock);
    }

    public static TheoryData<DateTime?> ClientSuppliedReceiptDates => new()
    {
        // Backdated a year (would dodge timely filing).
        new DateTime(2025, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        // In the future (the GKE demo symptom: claims "received" 11/28/2026 on 10/15/2026).
        new DateTime(2026, 11, 28, 0, 0, 0, DateTimeKind.Utc),
        // Absent.
        null,
    };

    [Theory]
    [MemberData(nameof(ClientSuppliedReceiptDates))]
    public async Task Submit_StampsReceiptDateFromClock_IgnoringClientValue(DateTime? clientDate)
    {
        var claim = BuildClaim();
        claim.SubmittedDate = clientDate ?? default;
        claim.ReceivedDate = clientDate;

        var result = await _sut.SubmitAsync(claim, "tenant-1", "actor", null);

        Assert.True(result.Success);
        var persisted = Assert.Single(_persisted);
        Assert.Equal(Now.UtcDateTime, persisted.SubmittedDate);
        Assert.Equal(Now.UtcDateTime, persisted.ReceivedDate);
        Assert.Equal(DateTimeKind.Utc, persisted.SubmittedDate.Kind);
        Assert.Equal(Now.UtcDateTime, result.Claim!.SubmittedDate);
    }

    [Fact]
    public async Task Submit_EachIntakeGetsTheClockAtItsOwnArrival()
    {
        await _sut.SubmitAsync(BuildClaim(), "tenant-1", "actor", null);
        _clock.Advance(TimeSpan.FromMinutes(7));
        await _sut.SubmitAsync(BuildClaim(), "tenant-1", "actor", null);

        Assert.Equal(2, _persisted.Count);
        Assert.Equal(Now.UtcDateTime, _persisted[0].SubmittedDate);
        Assert.Equal(Now.UtcDateTime.AddMinutes(7), _persisted[1].SubmittedDate);
        Assert.Equal(Now.UtcDateTime.AddMinutes(7), _persisted[1].ReceivedDate);
    }

    [Fact]
    public async Task Submit_KeepsSubmitterTransactionDateSeparateFromReceiptDate()
    {
        var claim = BuildClaim();
        var bht04 = new DateTime(2026, 1, 15);
        claim.SubmitterTransactionDate = bht04;
        claim.SubmittedDate = bht04;

        await _sut.SubmitAsync(claim, "tenant-1", "actor", null);

        var persisted = Assert.Single(_persisted);
        Assert.Equal(bht04, persisted.SubmitterTransactionDate);
        Assert.Equal(Now.UtcDateTime, persisted.SubmittedDate);
    }

    /// <summary>
    /// Adjustment intake: the corrected payload's dates are ignored and the
    /// new version's receipt date is when the correction arrived (what
    /// ClaimAdjustmentService has always modelled); the predecessor keeps
    /// its own original receipt date.
    /// </summary>
    [Fact]
    public async Task Adjustment_NewVersionStampedAtCorrectionReceipt_PredecessorUntouched()
    {
        var originalReceipt = new DateTime(2026, 5, 3, 9, 0, 0, DateTimeKind.Utc);
        var predecessor = new Claim
        {
            Id = "pred-1",
            TenantId = "t1",
            ClaimVersionId = "pred-1",
            VersionNumber = 1,
            VersionState = ClaimVersionState.Paid,
            Status = ClaimStatus.Paid,
            ClaimNumber = "CLM-001",
            MemberId = "m1",
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            SubmittedDate = originalReceipt,
            ReceivedDate = originalReceipt,
        };
        var claimRepository = Substitute.For<IClaimRepository>();
        claimRepository.GetByIdAsync("pred-1").Returns(predecessor);
        claimRepository.MarkSupersededProjectionAsync(
                "t1", "pred-1", Arg.Any<string>(), Arg.Any<DateTime>(), "actor-1", Arg.Any<CancellationToken>())
            .Returns(true);

        var adjustments = new ClaimAdjustmentService(
            claimRepository,
            Substitute.For<IClaimAdjustmentRepository>(),
            _sut,
            Substitute.For<IClaimVersionEventPublisher>(),
            Substitute.For<IMessageBus>(),
            NullLogger<ClaimAdjustmentService>.Instance);

        var corrected = BuildClaim();
        corrected.SubmittedDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        corrected.ReceivedDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await adjustments.CreateAdjustmentAsync(
            "pred-1",
            new ClaimAdjustmentRequest { AdjustmentReason = "Wrong code", CorrectedClaim = corrected },
            "idem-1", "t1", "actor-1", null);

        Assert.Equal(ClaimAdjustmentOutcome.Created, result.Outcome);
        var persisted = Assert.Single(_persisted);
        Assert.Equal(Now.UtcDateTime, persisted.SubmittedDate);
        Assert.Equal(Now.UtcDateTime, persisted.ReceivedDate);
        Assert.Equal(originalReceipt, predecessor.SubmittedDate);
        Assert.Equal(originalReceipt, predecessor.ReceivedDate);
    }

    private static AdapterClaim BuildClaim() => new()
    {
        ClaimNumber = "CLM-RCPT-1",
        MemberId = "MBR-1",
        BillingProviderNPI = "1234567893",
        ClaimType = ClaimType.Professional,
        ServiceDateFrom = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        ClaimLines = new List<AdapterClaimLine>
        {
            new() { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 150m, Units = 1 },
        },
    };

    internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
