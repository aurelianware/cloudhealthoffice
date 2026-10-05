using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentService.Controllers;
using PaymentService.Models;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// The real payment-service pipeline. Every caller needs a CHO token; the
/// tenant and the acting user come from it. Reads need payments:read, preparing
/// runs payments:run, ledger changes finance:write, and releasing money
/// (executing a payment or reversal run) payments:approve from a user other than
/// the run's creator; a service token never releases money. 835 downloads never
/// carry full bank numbers.
/// </summary>
public class PaymentPipelineAuthTests : IClassFixture<PaymentPipelineFactory>
{
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string Maker = "finance-maker-7";
    private const string Approver = "approver-9";

    // The service's wire format (string enums, AddCloudHealthOfficeJsonOptions).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PaymentPipelineFactory _f;
    private string? _tenantSeenByRepository;

    public PaymentPipelineAuthTests(PaymentPipelineFactory factory)
    {
        _f = factory;
        _f.Payments.ClearReceivedCalls();
        _f.Runs.ClearReceivedCalls();
        _f.ReversalRuns.ClearReceivedCalls();
        _f.Envelopes.ClearReceivedCalls();
        _f.Claims.Clear();

        _f.Payments.SearchAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(_ =>
            {
                _tenantSeenByRepository = Accessor().HttpContext?.Items["TenantId"] as string;
                return new List<Payment>();
            });
        _f.Payments.CreateAsync(default!).ReturnsForAnyArgs(ci =>
        {
            _tenantSeenByRepository = Accessor().HttpContext?.Items["TenantId"] as string;
            return ci.Arg<Payment>();
        });
        _f.Payments.UpdateAsync(default!).ReturnsForAnyArgs(ci => ci.Arg<Payment>());
        _f.Payments.GetByCheckNumberAsync(default!).ReturnsForAnyArgs((Payment?)null);
        _f.Runs.CreateAsync(default!).ReturnsForAnyArgs(ci => ci.Arg<PaymentRun>());
        _f.Runs.UpdateAsync(default!).ReturnsForAnyArgs(ci => ci.Arg<PaymentRun>());
        _f.Runs.SearchAsync(default, default, default).ReturnsForAnyArgs(new List<PaymentRun>());
        _f.ReversalRuns.CreateAsync(default!).ReturnsForAnyArgs(ci => ci.Arg<ReversalRun>());
        _f.ReversalRuns.UpdateAsync(default!).ReturnsForAnyArgs(ci => ci.Arg<ReversalRun>());
    }

    private IHttpContextAccessor Accessor() => _f.Services.GetRequiredService<IHttpContextAccessor>();

    private HttpClient As(string subject, params string[] roles)
    {
        var client = _f.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    /// <summary>A user token that may prepare payments (payments:read, payments:run) but holds no finance:write.</summary>
    private HttpClient PreparerWithoutFinanceWrite()
        => Bearer(ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            Maker, Tenant, new[] { "PaymentPreparer" }, new[] { "payments:read", "payments:run" }));

    private HttpClient Bearer(string token)
    {
        var client = _f.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ServiceToken(string clientId = "payment-scheduler")
        => Bearer(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));

    private PaymentRun PendingRun(string id, string? createdBy)
    {
        var run = new PaymentRun
        {
            Id = id,
            TenantId = Tenant,
            PaymentRunNumber = "PR-" + id,
            Status = PaymentRunStatus.Pending,
            CreatedBy = createdBy,
            NextCheckNumber = 1000000,
        };
        _f.Runs.GetByIdAsync(id).Returns(run);
        return run;
    }

    private ReversalRun PendingReversal(string id, string? createdBy)
    {
        var run = new ReversalRun
        {
            Id = id,
            TenantId = Tenant,
            ReversalRunNumber = "RR-" + id,
            Status = ReversalRunStatus.Pending,
            CreatedBy = createdBy,
            Criteria = new ReversalRunCriteria(),
        };
        _f.ReversalRuns.GetByIdAsync(id).Returns(run);
        return run;
    }

    // ── authentication and tenant ─────────────────────────────────────

    [Fact]
    public async Task NoToken_HeaderTenantOnly_Is401_AndRepositoryNotCalled()
    {
        var client = _f.CreateDefaultClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().SearchAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task NoToken_NoTenant_Is401_NeverADefaultTenant()
    {
        var response = await _f.CreateDefaultClient().GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().SearchAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task HeaderTenantDisagreeingWithToken_Is403()
    {
        var client = Bearer(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().SearchAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task Read_UsesTheTokenTenant()
    {
        var response = await As(Maker, ChoRolePermissions.Finance).GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Tenant, _tenantSeenByRepository);
    }

    [Fact]
    public async Task Read_WithoutPaymentsRead_Is403()
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(Maker, Tenant, new[] { "ClaimsOnly" }, new[] { "claims:read" });

        var response = await Bearer(token).GetAsync("/api/payments");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── payment runs: preparation ─────────────────────────────────────

    [Fact]
    public async Task CreateRun_CreatorIsTheTokenSubject_BodyCreatedByIgnored()
    {
        var response = await As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync("/api/paymentruns",
            new CreatePaymentRunRequest { CreatedBy = "someone-else" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _f.Runs.Received(1).CreateAsync(Arg.Is<PaymentRun>(r => r.CreatedBy == Maker));
    }

    [Fact]
    public async Task CancelRun_RecordsTheCancellerFromTheToken()
    {
        PendingRun("run-cancel", Maker);

        var response = await As(Maker, ChoRolePermissions.Finance).PostAsync("/api/paymentruns/run-cancel/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _f.Runs.Received(1).UpdateAsync(Arg.Is<PaymentRun>(r =>
            r.Status == PaymentRunStatus.Cancelled && r.CancelledBy == Maker));
    }

    // ── payment runs: releasing money ─────────────────────────────────

    [Fact]
    public async Task ExecuteRun_WithoutPaymentsApprove_Is403_AndNothingIsReleased()
    {
        PendingRun("run-a", "other-maker");

        var response = await As(Maker, ChoRolePermissions.Finance).PostAsync("/api/paymentruns/run-a/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.Runs.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteRun_ByItsCreator_Is403SeparationOfDuties_AndNothingIsReleased()
    {
        PendingRun("run-b", Approver);

        var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/paymentruns/run-b/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Separation of duties", await response.Content.ReadAsStringAsync());
        await _f.Runs.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteRun_WithServiceToken_Is403_AndNothingIsReleased()
    {
        PendingRun("run-c", Maker);

        var response = await ServiceToken().PostAsync("/api/paymentruns/run-c/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Separation of duties", await response.Content.ReadAsStringAsync());
        await _f.Runs.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteRun_BySecondUserWithPaymentsApprove_Releases_AndRecordsTheExecutor()
    {
        PendingRun("run-d", Maker);

        var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/paymentruns/run-d/execute", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _f.Runs.Received().UpdateAsync(Arg.Is<PaymentRun>(r => r.ExecutedBy == Approver));
        // After approval, claims-service is asked with payment-service's own
        // service token for the run's tenant, never the approver's token.
        var search = Assert.Single(_f.Claims.Requests);
        Assert.Equal("/api/claims/search", search.RequestUri!.AbsolutePath);
        Assert.Equal((Tenant, "payment-service"), NoCallerHost.TokenOf(search));
        Assert.Equal(Tenant, search.Headers.GetValues("X-Tenant-ID").Single());
    }

    [Fact]
    public async Task CreateAndExecuteRun_Is403_AndCreatesNothing()
    {
        var response = await As(Approver, ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/paymentruns/execute",
            new CreatePaymentRunRequest { CreatedBy = "someone-else" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Separation of duties", await response.Content.ReadAsStringAsync());
        await _f.Runs.DidNotReceiveWithAnyArgs().CreateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    // ── reversal runs ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateReversal_CreatorIsTheTokenSubject_BodyCreatedByIgnored()
    {
        var response = await As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync("/api/reversalruns",
            new CreateReversalRunRequest { CreatedBy = "someone-else" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _f.ReversalRuns.Received(1).CreateAsync(Arg.Is<ReversalRun>(r => r.CreatedBy == Maker));
    }

    [Fact]
    public async Task ExecuteReversal_WithoutPaymentsApprove_Is403()
    {
        PendingReversal("rr-a", "other-maker");

        var response = await As(Maker, ChoRolePermissions.Finance).PostAsync("/api/reversalruns/rr-a/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.ReversalRuns.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteReversal_ByItsCreator_Is403SeparationOfDuties()
    {
        PendingReversal("rr-b", Approver);

        var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/reversalruns/rr-b/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Separation of duties", await response.Content.ReadAsStringAsync());
        await _f.ReversalRuns.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteReversal_WithServiceToken_Is403()
    {
        PendingReversal("rr-c", Maker);

        var response = await ServiceToken().PostAsync("/api/reversalruns/rr-c/execute", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.ReversalRuns.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
        Assert.Empty(_f.Claims.Requests);
    }

    [Fact]
    public async Task ExecuteReversal_BySecondUser_Runs_AndRecordsTheExecutor()
    {
        PendingReversal("rr-d", Maker);
        _f.Claims.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"total":0,"page":1,"pageSize":200,"items":[]}"""),
        };
        try
        {
            var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/reversalruns/rr-d/execute", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await _f.ReversalRuns.Received().UpdateAsync(Arg.Is<ReversalRun>(r => r.ExecutedBy == Approver));
            var list = Assert.Single(_f.Claims.Requests);
            Assert.Equal("/api/v1/adjustments", list.RequestUri!.AbsolutePath);
            Assert.Equal((Tenant, "payment-service"), NoCallerHost.TokenOf(list));
        }
        finally
        {
            _f.Claims.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }
    }

    [Fact]
    public async Task CreateAndExecuteReversal_Is403_AndCreatesNothing()
    {
        var response = await As(Approver, ChoRolePermissions.TenantAdmin).PostAsJsonAsync("/api/reversalruns/execute",
            new CreateReversalRunRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.ReversalRuns.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    // ── payment ledger ────────────────────────────────────────────────

    private Payment StoredPayment(string id, PaymentStatus status)
    {
        var payment = new Payment
        {
            Id = id,
            TenantId = Tenant,
            CheckNumber = "0001000001",
            PaymentMethod = "ACH",
            TotalPaymentAmount = 250m,
            PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Cloud Health Office",
            PayeeName = "Clinic",
            PayeeNPI = "1234567893",
            Status = status,
        };
        _f.Payments.GetByIdAsync(id).Returns(payment);
        return payment;
    }

    [Fact]
    public async Task PostPayment_WithoutFinanceWrite_Is403()
    {
        StoredPayment("pay-p1", PaymentStatus.Received);

        var response = await PreparerWithoutFinanceWrite().PostAsJsonAsync("/api/payments/pay-p1/post",
            new PostPaymentRequest { PostedBy = Approver });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task PostPayment_PosterIsTheTokenSubject_BodyPostedByIgnored()
    {
        StoredPayment("pay-p2", PaymentStatus.Received);

        var response = await As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync("/api/payments/pay-p2/post",
            new PostPaymentRequest { PostedBy = "spoofed-user" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _f.Payments.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.PostedBy == Maker));
    }

    [Fact]
    public async Task ReconcilePayment_WithoutFinanceWrite_Is403()
    {
        StoredPayment("pay-r1", PaymentStatus.Posted);

        var response = await PreparerWithoutFinanceWrite().PostAsJsonAsync("/api/payments/pay-r1/reconcile",
            new ReconcilePaymentRequest());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    [Fact]
    public async Task ReconcilePayment_RecordsTheReconcilerFromTheToken()
    {
        StoredPayment("pay-r2", PaymentStatus.Posted);

        var response = await As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync("/api/payments/pay-r2/reconcile",
            new ReconcilePaymentRequest { Notes = "bank deposit" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _f.Payments.Received(1).UpdateAsync(Arg.Is<Payment>(p => p.ReconciledBy == Maker));
    }

    [Fact]
    public async Task RecordPayment_WithoutFinanceWrite_Is403()
    {
        var response = await PreparerWithoutFinanceWrite().PostAsJsonAsync("/api/payments",
            new Payment
            {
                CheckNumber = "CHK-1", PaymentMethod = "CHK", TotalPaymentAmount = 100m,
                PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
                PayerName = "Cloud Health Office", PayeeName = "Clinic", Status = PaymentStatus.Posted,
            }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _f.Payments.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task RecordPayment_AuditFieldsAndTenantFromToken_BodyValuesIgnored()
    {
        var response = await As(Maker, ChoRolePermissions.Finance).PostAsJsonAsync("/api/payments", new Payment
        {
            CheckNumber = "CHK-2",
            PaymentMethod = "CHK",
            TotalPaymentAmount = 100m,
            PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            PayerName = "Cloud Health Office",
            PayeeName = "Clinic",
            Status = PaymentStatus.Posted,
            PostedBy = "spoofed-user",
            TenantId = OtherTenant,
        }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Tenant, _tenantSeenByRepository);
        await _f.Payments.Received(1).CreateAsync(Arg.Is<Payment>(p => p.PostedBy == Maker && p.TenantId == Tenant));
    }

    // ── bank numbers in responses ─────────────────────────────────────

    private static void AssertNoFullBankNumbers(string body)
    {
        Assert.DoesNotContain(PaymentPipelineFactory.PayerRouting, body);
        Assert.DoesNotContain(PaymentPipelineFactory.PayerAccount, body);
        Assert.DoesNotContain(PaymentPipelineFactory.PayeeRouting, body);
        Assert.DoesNotContain(PaymentPipelineFactory.PayeeAccount, body);
    }

    [Fact]
    public async Task Era835Download_MasksBankRoutingAndAccountNumbers()
    {
        StoredPayment("pay-835", PaymentStatus.Posted);

        var response = await As(Maker, ChoRolePermissions.Finance).GetAsync("/api/payments/pay-835/835");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("BPR*C*250.00*C*ACH", body);
        AssertNoFullBankNumbers(body);
        Assert.Contains("XXXXXXXX9012", body); // payer account, last 4 kept
        Assert.Contains("XXXXX0015", body);    // payer routing, last 4 kept
    }

    [Fact]
    public async Task EraEnvelopeEdi_MasksBankRoutingAndAccountNumbers()
    {
        var edi = "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *260504*1200*^*00501*000000001*0*P*:~" +
                  "ST*835*0001*005010X221A1~" +
                  $"BPR*C*250.00*C*ACH*CCP*01*{PaymentPipelineFactory.PayerRouting}*DA*{PaymentPipelineFactory.PayerAccount}*20260504" +
                  $"*01*{PaymentPipelineFactory.PayeeRouting}*DA*{PaymentPipelineFactory.PayeeAccount}*20260504~" +
                  "SE*3*0001~IEA*1*000000001~";
        _f.Envelopes.GetByIdAsync("env-1").Returns(new EraEnvelopeRecord { Id = "env-1", TenantId = Tenant, EdiContent = edi });

        var response = await As(Maker, ChoRolePermissions.Finance).GetAsync("/api/v1/era-envelopes/env-1/edi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertNoFullBankNumbers(body);
        Assert.Contains("XXXXXXXX1098", body); // payee account, last 4 kept
        Assert.Contains("SE*3*0001~", body);   // the rest of the file is unchanged
    }
}
