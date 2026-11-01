using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Models.Tpl;
using ClaimsService.Repositories;
using ClaimsService.Services.Resolution;
using ClaimsService.Services.Tpl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Tpl;

public class TplRecoveryServiceTests
{
    private const string Tenant = "t1";
    private static readonly DateTime Paid = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly ITplClient _tpl = Substitute.For<ITplClient>();
    private readonly InMemoryCases _cases = new();

    private TplRecoveryService Service() => new(_claims, _cases, _tpl,
        Options.Create(new ThirdPartyLiabilityOptions()), NullLogger<TplRecoveryService>.Instance);

    private static Claim PaidClaim(string id, string state, string decision, params (string Code, decimal Paid)[] lines) => new()
    {
        Id = id,
        TenantId = Tenant,
        ClaimNumber = "CLM-" + id,
        MemberId = "M1",
        LineOfBusiness = LineOfBusiness.Medicaid,
        ClaimType = ClaimType.Professional,
        Status = ClaimStatus.Paid,
        ServiceDateFrom = new DateTime(2026, 2, 1),
        ServiceDateTo = new DateTime(2026, 2, 1),
        AdjudicatedDate = Paid,
        ClaimLines = lines.Select((l, i) => new ClaimLine
        {
            LineNumber = i + 1,
            ProcedureCode = l.Code,
            ChargeAmount = l.Paid,
            ServiceDateFrom = new DateTime(2026, 2, 1),
            AdjudicationResult = new LineAdjudicationResult { PaidAmount = l.Paid, AllowedAmount = l.Paid },
        }).ToList(),
        AdjudicationResult = new AdjudicationResult
        {
            PaymentDate = Paid,
            ThirdPartyLiability = decision == "none" ? null : new ClaimTplRecord
            {
                Decision = decision,
                Resources = [new ClaimTplResource { TplPolicyId = "p1", State = state, CarrierName = "Kaiser", PolicyNumber = "K1" }],
                PayAndChaseLines = decision == "PayAndChase"
                    ? lines.Select((_, i) => new ClaimTplPayAndChaseLine { LineNumber = i + 1, Reason = "code A" }).ToList()
                    : [],
            },
        },
    };

    private void Finalized(params Claim[] claims) =>
        _claims.FindFinalizedMedicaidAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(claims);

    [Fact]
    public async Task Sync_OpensACaliforniaCaseWithATwelveMonthWindow_Once()
    {
        Finalized(PaidClaim("c1", "CA", "PayAndChase", ("99213", 80m), ("85025", 20m)));

        var first = await Service().SyncPayAndChaseAsync(Tenant, Paid.AddDays(-1), Paid.AddDays(1), 100, "ops");
        var second = await Service().SyncPayAndChaseAsync(Tenant, Paid.AddDays(-1), Paid.AddDays(1), 100, "ops");

        Assert.Equal(1, first.CasesOpened);
        Assert.Equal(100m, first.AmountOpened);
        Assert.Equal(0, second.CasesOpened);
        Assert.Equal(1, second.AlreadyOpen);
        var c = Assert.Single(_cases.All);
        Assert.Equal("CA", c.State);
        Assert.Equal(new DateTime(2027, 3, 10), c.RetentionDeadline);
        Assert.Equal(TplRecoveryOrigin.PayAndChase, c.Origin);
        Assert.Equal("K1", c.Resources.Single().PolicyNumber);
    }

    [Fact]
    public async Task Sync_TexasHasNoDefaultWindow()
    {
        Finalized(PaidClaim("c1", "TX", "PayAndChase", ("D0120", 30m)));

        await Service().SyncPayAndChaseAsync(Tenant, Paid.AddDays(-1), Paid.AddDays(1), 100, "ops");

        Assert.Null(Assert.Single(_cases.All).RetentionDeadline);
    }

    [Fact]
    public async Task Retroactive_OpensACaseForAPaidClaimTheNewTplCovers()
    {
        Finalized(
            PaidClaim("c1", "CA", "none", ("99213", 75m)),
            PaidClaim("c2", "CA", "PayAndChase", ("99213", 75m)));
        _tpl.GetActiveAsync(Tenant, "M1", Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TplResource
                {
                    Id = "p9", State = "CA", CarrierName = "Blue Shield", Scope = TplServiceScope.Comprehensive,
                    EffectiveDate = new DateTime(2026, 1, 1),
                },
            });

        var result = await Service().CheckRetroactiveAsync(Tenant, ["M1"], new DateTime(2026, 6, 1), "ops");

        Assert.Equal(1, result.ClaimsChecked);
        Assert.Equal(1, result.CasesOpened);
        var c = Assert.Single(_cases.All);
        Assert.Equal("c1", c.ClaimId);
        Assert.Equal(TplRecoveryOrigin.Retroactive, c.Origin);
        Assert.Equal(75m, c.AmountPaid);
        Assert.Contains("Blue Shield", c.Lines.Single().Reason);
    }

    [Fact]
    public void Events_MoveTheCaseThroughItsStatuses()
    {
        var c = new TplRecoveryCase { AmountPaid = 100m, RetentionDeadline = new DateTime(2027, 3, 10) };

        TplRecoveryService.Apply(c, new(TplRecoveryEventType.Billed, null, null, "INV-1", null), "ops");
        Assert.Equal(TplRecoveryStatus.Billed, c.Status);

        TplRecoveryService.Apply(c, new(TplRecoveryEventType.PaymentReceived, 60m, null, "EFT-1", null), "ops");
        Assert.Equal(TplRecoveryStatus.PartiallyRecovered, c.Status);
        Assert.Equal(40m, c.AmountOutstanding);

        TplRecoveryService.Apply(c, new(TplRecoveryEventType.WrittenOff, 40m, null, null, "below threshold"), "ops");
        Assert.Equal(TplRecoveryStatus.Recovered, c.Status);
        Assert.Equal(0m, c.AmountOutstanding);

        Assert.Throws<InvalidOperationException>(() =>
            TplRecoveryService.Apply(c, new(TplRecoveryEventType.PaymentReceived, 1m, null, null, null), "ops"));
        Assert.Throws<ArgumentException>(() =>
            TplRecoveryService.Apply(new TplRecoveryCase { AmountPaid = 5m },
                new(TplRecoveryEventType.PaymentReceived, null, null, null, null), "ops"));
    }

    [Fact]
    public async Task Clocks_HandExpiredCasesToTheState_UnlessARepaymentPlanWasAgreedInTime()
    {
        var expired = new TplRecoveryCase
        {
            TenantId = Tenant, CaseKey = "a", State = "CA", AmountPaid = 50m, RetentionDeadline = new DateTime(2027, 3, 10),
        };
        var planned = new TplRecoveryCase
        {
            TenantId = Tenant, CaseKey = "b", State = "CA", AmountPaid = 50m, RetentionDeadline = new DateTime(2027, 3, 10),
            RepaymentPlanAgreedAt = new DateTime(2027, 2, 1),
        };
        var lateplan = new TplRecoveryCase
        {
            TenantId = Tenant, CaseKey = "c", State = "CA", AmountPaid = 50m, RetentionDeadline = new DateTime(2027, 3, 10),
            RepaymentPlanAgreedAt = new DateTime(2027, 4, 1),
        };
        await _cases.InsertManyAsync([expired, planned, lateplan]);

        var result = await Service().ApplyClocksAsync(Tenant, new DateTime(2027, 3, 15), "ops");

        Assert.Equal(2, result.TransferredToState);
        Assert.Equal(1, result.KeptUnderRepaymentPlan);
        Assert.Equal(TplRecoveryStatus.TransferredToState, expired.Status);
        Assert.Equal(new DateTime(2027, 3, 11), expired.TransferredToStateAt);
        Assert.Equal(TplRecoveryStatus.Open, planned.Status);
        Assert.Equal(TplRecoveryStatus.TransferredToState, lateplan.Status);
    }

    [Fact]
    public async Task Report_ListsTransfersAndRecoveriesInTheMonth()
    {
        var c = new TplRecoveryCase
        {
            TenantId = Tenant, CaseKey = "a", State = "CA", ClaimNumber = "CLM-1", MemberId = "M1", AmountPaid = 50m,
            Resources = [new TplRecoveryResource { CarrierName = "Kaiser, Inc", PolicyNumber = "K1" }],
        };
        TplRecoveryService.Apply(c, new(TplRecoveryEventType.PaymentReceived, 20m, new DateTime(2027, 2, 5), null, null), "ops");
        TplRecoveryService.Apply(c, new(TplRecoveryEventType.TransferredToState, null, new DateTime(2027, 2, 20), null, null), "ops");
        await _cases.InsertManyAsync([c]);

        var csv = await Service().BuildStateReportCsvAsync(Tenant, "CA", new DateTime(2027, 2, 1));
        var rows = csv.Trim().Split('\n');

        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, r => r.Contains("recovery_received") && r.Contains("20.00"));
        Assert.Contains(rows, r => r.Contains("transferred_to_state") && r.Contains("30.00"));
        Assert.Contains("\"Kaiser, Inc\"", csv);
    }

    private sealed class InMemoryCases : ITplRecoveryRepository
    {
        private readonly List<TplRecoveryCase> _rows = new();
        public IReadOnlyList<TplRecoveryCase> All => _rows;

        public Task<TplRecoveryCase?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
            Task.FromResult(_rows.FirstOrDefault(r => r.TenantId == tenantId && r.Id == id));

        public Task<IReadOnlySet<string>> ExistingKeysAsync(string tenantId, IReadOnlyCollection<string> caseKeys, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(_rows.Where(r => r.TenantId == tenantId && caseKeys.Contains(r.CaseKey))
                .Select(r => r.CaseKey).ToHashSet());

        public Task<IReadOnlyList<TplRecoveryCase>> ListAsync(string tenantId, TplRecoveryQuery q, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TplRecoveryCase>>(_rows.Where(r =>
                r.TenantId == tenantId
                && (q.State is null || r.State == q.State)
                && (!q.OpenOnly || !r.IsClosed)
                && (q.DeadlineBefore is null || r.RetentionDeadline < q.DeadlineBefore)).ToList());

        public Task InsertManyAsync(IReadOnlyCollection<TplRecoveryCase> cases, CancellationToken ct = default)
        {
            _rows.AddRange(cases);
            return Task.CompletedTask;
        }

        public Task SaveAsync(TplRecoveryCase recoveryCase, CancellationToken ct = default) => Task.CompletedTask;
    }
}
