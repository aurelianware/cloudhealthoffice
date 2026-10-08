using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using PaymentService.Models;
using PaymentService.Repositories;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// Concurrency and the trading-partner rule, on the real pipeline with the
/// stand-in claims-service. Rendezvous hooks in the in-memory stores hold both
/// executions at the same point (after reading the run, or after the "already
/// paid" lookup), so each test is the worst-case interleaving every time.
/// </summary>
public sealed class RunConcurrencyTests : IDisposable
{
    private const string Maker = "finance-maker-1";
    private const string ApproverA = "finance-approver-2";
    private const string ApproverB = "finance-approver-3";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RunExecutionHost _host = new();

    public void Dispose() => _host.Dispose();

    /// <summary>Holds callers until <paramref name="parties"/> of them arrived (later callers pass straight through).</summary>
    private static Func<Task> Rendezvous(int parties)
    {
        var count = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return async () =>
        {
            if (Interlocked.Increment(ref count) >= parties)
                all.TrySetResult();
            await all.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
    }

    private HttpClient Approver(string subject) => _host.As(subject, ChoRolePermissions.FinanceApprover);

    private async Task<string> CreateAsync(string path)
    {
        var response = await _host.As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync(path, new { criteria = new { } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private List<Payment> PaymentsFor(string claimId, bool reversal = false)
        => _host.Payments.All.Where(p => p.IsReversal == reversal && p.ClaimPayments.Any(cp => cp.ClaimId == claimId)).ToList();

    // ── atomic start ──────────────────────────────────────────────────

    [Fact]
    public async Task SameRun_TwoConcurrentExecutions_OneSucceeds_OtherIs409_AndMakesNoClaimsCall()
    {
        _host.Claims.Add("clm-1");
        var runId = await CreateAsync("/api/paymentruns");
        _host.Runs.AfterGet = Rendezvous(2); // both read the run while it is Pending

        var responses = await Task.WhenAll(
            Approver(ApproverA).PostAsync($"/api/paymentruns/{runId}/execute", null),
            Approver(ApproverB).PostAsync($"/api/paymentruns/{runId}/execute", null));
        _host.Runs.AfterGet = null;

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        // The executing run searches twice (Approved claims, Denied claims); the other run never searches.
        Assert.Equal(2, _host.Claims.Calls.Count(c => c.Path == "/api/claims/search"));
        Assert.Single(PaymentsFor("clm-1"));
        Assert.Equal(1, _host.Claims.Get("clm-1").FinalizeCount);
    }

    [Fact]
    public async Task SameReversalRun_TwoConcurrentExecutions_OneSucceeds_OtherIs409()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        var runId = await CreateAsync("/api/reversalruns");
        _host.ReversalRuns.AfterGet = Rendezvous(2);

        var responses = await Task.WhenAll(
            Approver(ApproverA).PostAsync($"/api/reversalruns/{runId}/execute", null),
            Approver(ApproverB).PostAsync($"/api/reversalruns/{runId}/execute", null));
        _host.ReversalRuns.AfterGet = null;

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(_host.Claims.Calls, c => c.Path == "/api/v1/adjustments");
        Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(1, _host.Claims.Get("clm-r1").VoidCount);
    }

    [Fact]
    public async Task ExecutingACompletedRunAgain_Is409()
    {
        _host.Claims.Add("clm-1");
        var runId = await CreateAsync("/api/paymentruns");
        Assert.Equal(HttpStatusCode.OK, (await Approver(ApproverA).PostAsync($"/api/paymentruns/{runId}/execute", null)).StatusCode);
        var callsBefore = _host.Claims.Calls.Count;

        var again = await Approver(ApproverB).PostAsync($"/api/paymentruns/{runId}/execute", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(callsBefore, _host.Claims.Calls.Count);
    }

    // ── one payment per claim ─────────────────────────────────────────

    [Fact]
    public async Task TwoRuns_OverlappingClaims_Concurrently_EachClaimPaidOnce()
    {
        _host.Claims.Add("clm-1");
        _host.Claims.Add("clm-2", npi: "1234567894");
        _host.Claims.Add("clm-3", npi: "1234567895");
        var runA = await CreateAsync("/api/paymentruns");
        var runB = await CreateAsync("/api/paymentruns");
        // Both runs have done the "already paid" lookup (nothing paid yet)
        // before either creates a payment.
        _host.Payments.AfterPaidLookup = Rendezvous(2);

        var responses = await Task.WhenAll(
            Approver(ApproverA).PostAsync($"/api/paymentruns/{runA}/execute", null),
            Approver(ApproverB).PostAsync($"/api/paymentruns/{runB}/execute", null));
        _host.Payments.AfterPaidLookup = null;

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var runs = new List<PaymentRun>();
        foreach (var r in responses)
            runs.Add((await r.Content.ReadFromJsonAsync<PaymentRun>(Json))!);

        foreach (var claimId in new[] { "clm-1", "clm-2", "clm-3" })
        {
            Assert.Single(PaymentsFor(claimId));
            Assert.Equal(1, _host.Claims.Get(claimId).FinalizeCount);
            // Paid by exactly one run; the other lists it as already paid.
            Assert.Single(runs, run => run.ClaimIds.Contains(claimId));
            Assert.Single(runs, run => run.AlreadyPaidClaimIds.Contains(claimId));
        }
        Assert.Equal(3, _host.Reservations.All.Count(r => r.Kind == ClaimReservationKind.Payment));
    }

    [Fact]
    public async Task TwoReversalRuns_SameAdjustment_Concurrently_ReversedOnce()
    {
        _host.Claims.Add("clm-r1", status: "Paid");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        var runA = await CreateAsync("/api/reversalruns");
        var runB = await CreateAsync("/api/reversalruns");
        _host.Payments.AfterPaidLookup = Rendezvous(2);

        var responses = await Task.WhenAll(
            Approver(ApproverA).PostAsync($"/api/reversalruns/{runA}/execute", null),
            Approver(ApproverB).PostAsync($"/api/reversalruns/{runB}/execute", null));
        _host.Payments.AfterPaidLookup = null;

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var runs = new List<ReversalRun>();
        foreach (var r in responses)
            runs.Add((await r.Content.ReadFromJsonAsync<ReversalRun>(Json))!);

        Assert.Single(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal(1, _host.Claims.Get("clm-r1").VoidCount);
        Assert.Single(runs, run => run.PaymentIds.Count == 1);
        Assert.Single(runs, run => run.AlreadyReversedClaimIds.Contains("clm-r1"));
    }

    // ── no payment without a trading partner ──────────────────────────

    [Fact]
    public async Task MissingTradingPartner_ClaimNotPaid_Listed_PaidOnceAPartnerExists()
    {
        _host.Claims.Add("clm-ok");
        _host.Claims.Add("clm-no-tp", npi: "9999999991");
        _host.TradingPartners.MissingNpis.Add("9999999991");

        var response = await Approver(ApproverA).PostAsync($"/api/paymentruns/{await CreateAsync("/api/paymentruns")}/execute", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<PaymentRun>(Json))!;

        Assert.Equal(new[] { "clm-no-tp" }, run.NeedsTradingPartnerClaimIds);
        Assert.Contains(run.Warnings, w => w.Contains("clm-no-tp") && w.Contains("no trading partner"));
        Assert.Equal(new[] { "clm-ok" }, run.ClaimIds);
        Assert.Empty(PaymentsFor("clm-no-tp"));
        Assert.Empty(run.PendingFinalizeClaimIds);
        Assert.Equal("Approved", _host.Claims.Get("clm-no-tp").Status);
        Assert.DoesNotContain(_host.Reservations.All, r => r.ClaimId == "clm-no-tp");

        // Once the provider has a trading partner, the next run pays it.
        _host.TradingPartners.MissingNpis.Clear();
        var next = await Approver(ApproverA).PostAsync($"/api/paymentruns/{await CreateAsync("/api/paymentruns")}/execute", null);
        var nextRun = (await next.Content.ReadFromJsonAsync<PaymentRun>(Json))!;

        Assert.Equal(new[] { "clm-no-tp" }, nextRun.ClaimIds);
        Assert.Single(PaymentsFor("clm-no-tp"));
        Assert.Equal("Paid", _host.Claims.Get("clm-no-tp").Status);
    }

    [Fact]
    public async Task MissingTradingPartner_ReversalNotRecouped_AdjustmentStaysPending()
    {
        _host.Claims.Add("clm-r1", status: "Paid", npi: "9999999992");
        _host.Claims.AddPendingReversal("adj-1", "clm-r1");
        _host.SeedOriginalPayment("clm-r1");
        _host.TradingPartners.MissingNpis.Add("9999999992");

        var response = await Approver(ApproverA).PostAsync($"/api/reversalruns/{await CreateAsync("/api/reversalruns")}/execute", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<ReversalRun>(Json))!;

        Assert.Equal(new[] { "clm-r1" }, run.NeedsTradingPartnerClaimIds);
        Assert.Empty(run.PaymentIds);
        Assert.Empty(PaymentsFor("clm-r1", reversal: true));
        Assert.Equal("Paid", _host.Claims.Get("clm-r1").Status);
        Assert.Equal(ClaimAdjustmentDtoStatus.PendingReversal, _host.Claims.Adjustments["adj-1"].Status);
    }
}
