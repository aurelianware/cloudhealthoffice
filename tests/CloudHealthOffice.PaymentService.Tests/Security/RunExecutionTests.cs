using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using PaymentService.Models;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// Approved runs complete end to end against stand-ins that enforce claims and
/// trading-partner permissions: after payments:approve and separation of duties,
/// the run's downstream calls carry payment-service's service token (the
/// approver, a FinanceApprover, holds no claims permission), while the approver
/// stays the recorded actor. And a claim payment-service already paid is never
/// paid (or reversed) again, whatever claims-service says.
/// </summary>
public sealed class RunExecutionTests : IDisposable
{
    private const string Tenant = RunExecutionHost.Tenant;
    private const string Maker = "finance-maker-1";
    private const string Approver = "finance-approver-2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RunExecutionHost _host = new();

    public void Dispose() => _host.Dispose();

    private HttpClient MakerClient() => _host.As(Maker, ChoRolePermissions.Finance);
    private HttpClient ApproverClient() => _host.As(Approver, ChoRolePermissions.FinanceApprover);

    private async Task<string> CreatePaymentRunAsync()
    {
        var response = await MakerClient().PostAsJsonAsync("/api/paymentruns", new { criteria = new { } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PaymentRun>(Json))!.Id;
    }

    private async Task<string> CreateReversalRunAsync()
    {
        var response = await MakerClient().PostAsJsonAsync("/api/reversalruns", new { criteria = new { } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReversalRun>(Json))!.Id;
    }

    private async Task<PaymentRun> ExecutePaymentRunAsync(string id)
    {
        var response = await ApproverClient().PostAsync($"/api/paymentruns/{id}/execute", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PaymentRun>(Json))!;
    }

    private async Task<ReversalRun> ExecuteReversalRunAsync(string id)
    {
        var response = await ApproverClient().PostAsync($"/api/reversalruns/{id}/execute", null);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReversalRun>(Json))!;
    }

    private List<Payment> PaymentsFor(string claimId, bool reversal = false)
        => _host.Payments.All.Where(p => p.IsReversal == reversal && p.ClaimPayments.Any(cp => cp.ClaimId == claimId)).ToList();

    private static void AssertServiceToken(RecordedCall call)
    {
        Assert.True(call.IsService, $"{call.Method} {call.Path} did not carry a service token (sub {call.Subject})");
        Assert.Equal(RunExecutionHost.ServiceClientId, call.Subject);
        Assert.Equal(RunExecutionHost.ServiceClientId, call.AuthorizedParty);
        Assert.Equal(Tenant, call.TokenTenant);
        Assert.NotEqual(Approver, call.Subject);
    }

    // ── 1. Approved runs call claims-service with the service token ─────

    [Fact]
    public async Task FinanceApprover_ExecutesPaymentRun_EndToEnd_DownstreamCallsCarryTheServiceToken()
    {
        _host.Claims.Add("clm-1");
        _host.Claims.Add("clm-2", approved: 250m);
        var runId = await CreatePaymentRunAsync();

        var run = await ExecutePaymentRunAsync(runId);

        Assert.Equal(PaymentRunStatus.Completed, run.Status);
        Assert.Empty(run.Errors);
        Assert.Empty(run.PendingFinalizeClaimIds);
        Assert.Equal(2, run.TotalClaims);
        Assert.Equal(350m, run.TotalPaymentAmount);

        // claims-service: search plus one remittance per claim, all as payment-service.
        var claimCalls = _host.Claims.Calls;
        Assert.Contains(claimCalls, c => c.Path == "/api/claims/search");
        Assert.Equal(2, claimCalls.Count(c => c.Path.EndsWith("/remittance")));
        Assert.All(claimCalls, c => Assert.Equal(HttpStatusCode.OK, c.Status));
        Assert.All(claimCalls, AssertServiceToken);
        Assert.Equal("Paid", _host.Claims.Get("clm-1").Status);
        Assert.Equal("Paid", _host.Claims.Get("clm-2").Status);

        // trading-partner-service too (FinanceApprover has no trading-partners:read).
        Assert.NotEmpty(_host.TradingPartners.Calls);
        Assert.All(_host.TradingPartners.Calls, c => Assert.Equal(HttpStatusCode.OK, c.Status));
        Assert.All(_host.TradingPartners.Calls, AssertServiceToken);

        // The approver is the actor on the run, the payments and the 835s.
        Assert.Equal(Approver, run.ExecutedBy);
        Assert.Equal(Maker, run.CreatedBy);
        var payment = Assert.Single(_host.Payments.All);
        Assert.Equal(Approver, payment.PostedBy);
        Assert.Equal(PaymentStatus.Posted, payment.Status);
        Assert.All(payment.ClaimPayments, cp => Assert.NotNull(cp.FinalizedAt));
        var envelope = Assert.Single(_host.Envelopes.All);
        Assert.Equal(Approver, envelope.CreatedBy);
        Assert.Equal(envelope.Id, payment.EraEnvelopeId);
    }

    [Fact]
    public async Task FinanceApprover_ExecutesReversalRun_EndToEnd_VoidCarriesServiceToken_ReasonNamesApprover()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        var runId = await CreateReversalRunAsync();

        var run = await ExecuteReversalRunAsync(runId);

        Assert.Equal(ReversalRunStatus.Completed, run.Status);
        Assert.Equal(new[] { "adj-1" }, run.AdjustmentIds);
        Assert.Empty(run.PendingVoidClaimIds);
        Assert.Equal(Approver, run.ExecutedBy);
        Assert.All(_host.Claims.Calls, c => Assert.Equal(HttpStatusCode.OK, c.Status));
        Assert.All(_host.Claims.Calls, AssertServiceToken);
        Assert.Contains(_host.Claims.Calls, c => c.Path == "/api/claims/clm-r1/void");

        var claim = _host.Claims.Get("clm-r1");
        Assert.Equal("Voided", claim.Status);
        Assert.Contains($"released by {Approver}", claim.VoidReason);
        var reversal = Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(Approver, reversal.PostedBy);
        Assert.Equal(PaymentStatus.Posted, reversal.Status);
        Assert.All(_host.Envelopes.All, e => Assert.Equal(Approver, e.CreatedBy));
    }

    [Fact]
    public async Task RunCreator_Executing_Is403_AndNoDownstreamCall()
    {
        _host.Claims.Add("clm-1");
        // One user who may both prepare (payments:run) and approve.
        var admin = _host.As("tenant-admin-4", ChoRolePermissions.TenantAdmin);
        var response = await admin.PostAsJsonAsync("/api/paymentruns", new { criteria = new { } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<PaymentRun>(Json))!.Id;

        var execute = await admin.PostAsync($"/api/paymentruns/{runId}/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
        Assert.Contains("Separation of duties", await execute.Content.ReadAsStringAsync());
        Assert.Empty(_host.Claims.Calls);
        Assert.Empty(_host.Payments.All);
    }

    [Fact]
    public async Task UserWithoutPaymentsApprove_Executing_Is403_AndNoClaimsCall()
    {
        _host.Claims.Add("clm-1");
        var runId = await CreatePaymentRunAsync();

        // A different Finance user: payments:run, claims:read, but no payments:approve.
        var execute = await _host.As("finance-other-3", ChoRolePermissions.Finance)
            .PostAsync($"/api/paymentruns/{runId}/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
        Assert.Empty(_host.Claims.Calls);
        Assert.Empty(_host.TradingPartners.Calls);
        Assert.Empty(_host.Payments.All);
        Assert.Equal(PaymentRunStatus.Pending, (await _host.Runs.GetByIdAsync(runId))!.Status);
    }

    [Fact]
    public async Task ServiceToken_Executing_Is403_AndNoClaimsCall()
    {
        _host.Claims.Add("clm-1");
        var runId = await CreatePaymentRunAsync();

        var execute = await _host.AsService().PostAsync($"/api/paymentruns/{runId}/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
        Assert.Empty(_host.Claims.Calls);
        Assert.Empty(_host.Payments.All);
    }

    // ── 2. Paid claims are never selected again ───────────────────────

    [Fact]
    public async Task FinalizeFails_PaymentIsPaidPendingFinalize_RunListsIt_NextRunDoesNotPayAgain()
    {
        _host.Claims.Add("clm-1");
        _host.Claims.Add("clm-2");
        _host.Claims.FailRemittance.Add("clm-2");

        var first = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());

        Assert.Equal(PaymentRunStatus.Completed, first.Status);
        Assert.Equal(new[] { "clm-2" }, first.PendingFinalizeClaimIds);
        Assert.Contains(first.Errors, e => e.Contains("clm-2") && e.Contains("PaidPendingFinalize"));
        var payment = Assert.Single(_host.Payments.All);
        Assert.Equal(PaymentStatus.PaidPendingFinalize, payment.Status);
        Assert.NotNull(payment.ClaimPayments.Single(cp => cp.ClaimId == "clm-1").FinalizedAt);
        Assert.Null(payment.ClaimPayments.Single(cp => cp.ClaimId == "clm-2").FinalizedAt);
        Assert.Equal("Approved", _host.Claims.Get("clm-2").Status); // claims-service still offers it

        // A second run (claims-service still failing) does not pay clm-2 again.
        var second = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());

        Assert.Equal(PaymentRunStatus.Completed, second.Status);
        Assert.Contains("clm-2", second.AlreadyPaidClaimIds);
        Assert.Empty(second.PaymentIds);
        Assert.Single(PaymentsFor("clm-2"));
        Assert.Equal(0m, second.TotalPaymentAmount);
    }

    [Fact]
    public async Task RetryEndpoint_FinalizesPendingClaims_WithoutNewPayments_SameCheckNumber()
    {
        _host.Claims.Add("clm-1");
        _host.Claims.FailRemittance.Add("clm-1");
        var first = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());
        Assert.Equal(new[] { "clm-1" }, first.PendingFinalizeClaimIds);
        var payment = Assert.Single(_host.Payments.All);

        _host.Claims.FailRemittance.Clear();
        var retry = await MakerClient().PostAsync($"/api/paymentruns/{first.Id}/finalize", null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var run = (await retry.Content.ReadFromJsonAsync<PaymentRun>(Json))!;
        Assert.Empty(run.PendingFinalizeClaimIds);
        Assert.Single(_host.Payments.All);
        Assert.Equal(PaymentStatus.Posted, _host.Payments.All.Single().Status);
        var claim = _host.Claims.Get("clm-1");
        Assert.Equal("Paid", claim.Status);
        Assert.Equal(payment.CheckNumber, claim.CheckNumber);
        Assert.Equal(1, claim.FinalizeCount);
        Assert.All(_host.Claims.Calls, AssertServiceToken);

        // Retrying again is a no-op: nothing pending, nothing sent, nothing created.
        var callsBefore = _host.Claims.Calls.Count;
        var again = await MakerClient().PostAsync($"/api/paymentruns/{first.Id}/finalize", null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(callsBefore, _host.Claims.Calls.Count);
        Assert.Single(_host.Payments.All);
    }

    [Fact]
    public async Task NextRun_RetriesPendingFinalize_WithoutNewPayment()
    {
        _host.Claims.Add("clm-1");
        _host.Claims.FailRemittance.Add("clm-1");
        var first = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());
        var checkNumber = _host.Payments.All.Single().CheckNumber;

        _host.Claims.FailRemittance.Clear();
        var second = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());

        Assert.Empty(second.PaymentIds);
        Assert.Contains("clm-1", second.AlreadyPaidClaimIds);
        Assert.Single(_host.Payments.All);
        Assert.Equal(PaymentStatus.Posted, _host.Payments.All.Single().Status);
        Assert.Equal(checkNumber, _host.Claims.Get("clm-1").CheckNumber);
        Assert.Empty((await _host.Runs.GetByIdAsync(first.Id))!.PendingFinalizeClaimIds);
    }

    [Fact]
    public async Task RetryEndpoint_NeedsPaymentsRun()
    {
        var runId = await CreatePaymentRunAsync();

        // FinanceApprover holds payments:approve but not payments:run.
        var response = await ApproverClient().PostAsync($"/api/paymentruns/{runId}/finalize", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateSelectionGuard_RepeatedClaimPaidOnce_ClaimAlreadyPaidInPaymentServiceSkipped()
    {
        _host.Claims.Add("clm-dup");
        _host.Claims.Add("clm-paid-elsewhere");
        _host.Claims.DuplicateSearchResults = true;
        // payment-service already paid clm-paid-elsewhere (e.g. a run whose
        // finalize never reached claims-service); claims-service says Approved.
        await _host.Payments.CreateAsync(new Payment
        {
            CheckNumber = "0000999999",
            TotalPaymentAmount = 100m,
            PaymentDate = DateTime.UtcNow,
            PayerName = "Cloud Health Office",
            PayeeName = "Clinic",
            Status = PaymentStatus.Posted,
            ClaimPayments = new List<ClaimPayment> { new() { ClaimId = "clm-paid-elsewhere", PatientControlNumber = "CN", ClaimStatusCode = "1", PaymentAmount = 100m } },
        });

        var run = await ExecutePaymentRunAsync(await CreatePaymentRunAsync());

        Assert.Equal(1, run.TotalClaims);
        Assert.Equal(new[] { "clm-dup" }, run.ClaimIds);
        Assert.Equal(100m, run.TotalPaymentAmount);
        Assert.Single(PaymentsFor("clm-dup"));
        Assert.Single(PaymentsFor("clm-dup").Single().ClaimPayments);
        Assert.Single(PaymentsFor("clm-paid-elsewhere"));
        Assert.Contains("clm-paid-elsewhere", run.AlreadyPaidClaimIds);
        Assert.Equal(1, _host.Claims.Get("clm-dup").FinalizeCount);
    }

    [Fact]
    public async Task VoidFails_NextReversalRun_DoesNotReverseAgain_RetriesTheVoid()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        _host.Claims.FailVoid.Add("clm-r1");

        var first = await ExecuteReversalRunAsync(await CreateReversalRunAsync());

        Assert.Equal(ReversalRunStatus.Completed, first.Status);
        Assert.Equal(new[] { "clm-r1" }, first.PendingVoidClaimIds);
        Assert.Contains(first.Errors, e => e.Contains("clm-r1"));
        var reversal = Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(PaymentStatus.PaidPendingFinalize, reversal.Status);
        Assert.Equal("Paid", _host.Claims.Get("clm-r1").Status);

        // The adjustment is still PendingReversal; a second run picks it up.
        var stillFailing = await ExecuteReversalRunAsync(await CreateReversalRunAsync());
        Assert.Contains("clm-r1", stillFailing.AlreadyReversedClaimIds);
        Assert.Empty(stillFailing.PaymentIds);
        Assert.Single(PaymentsFor("clm-r1", reversal: true));

        _host.Claims.FailVoid.Clear();
        var third = await ExecuteReversalRunAsync(await CreateReversalRunAsync());

        Assert.Empty(third.PaymentIds);
        Assert.Equal(new[] { "adj-1" }, third.AdjustmentIds);
        Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(PaymentStatus.Posted, PaymentsFor("clm-r1", reversal: true).Single().Status);
        Assert.Equal("Voided", _host.Claims.Get("clm-r1").Status);
        Assert.Equal(1, _host.Claims.Get("clm-r1").VoidCount);
        Assert.Empty((await _host.ReversalRuns.GetByIdAsync(first.Id))!.PendingVoidClaimIds);
    }

    [Fact]
    public async Task ReversalRetryEndpoint_VoidsPending_WithoutNewReversalPayment()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        _host.Claims.FailVoid.Add("clm-r1");
        var first = await ExecuteReversalRunAsync(await CreateReversalRunAsync());

        _host.Claims.FailVoid.Clear();
        var retry = await MakerClient().PostAsync($"/api/reversalruns/{first.Id}/void", null);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var run = (await retry.Content.ReadFromJsonAsync<ReversalRun>(Json))!;
        Assert.Empty(run.PendingVoidClaimIds);
        Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal("Voided", _host.Claims.Get("clm-r1").Status);
        Assert.Equal(1, _host.Claims.Get("clm-r1").VoidCount);
    }

    [Fact]
    public async Task SameRun_TwoAdjustmentsForOnePredecessor_ReversedOnce()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        _host.Claims.AddPendingReversal("adj-2", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");

        var run = await ExecuteReversalRunAsync(await CreateReversalRunAsync());

        Assert.Single(run.PaymentIds);
        Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(1, _host.Claims.Get("clm-r1").VoidCount);
    }
}
