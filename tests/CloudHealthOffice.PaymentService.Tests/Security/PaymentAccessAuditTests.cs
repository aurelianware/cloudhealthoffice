using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Audit;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PaymentService.Models;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// Access accounting for payments: who read a payment (by id or check number),
/// refused reads, and the payment's access log (audit:read; reading it is logged).
/// </summary>
public class PaymentAccessAuditTests : IClassFixture<PaymentPipelineFactory>
{
    private const string Tenant = "tenant-1";

    private readonly WebApplicationFactory<Program> _host;
    private readonly RecordAccessStoreInMemory _log = new();

    public PaymentAccessAuditTests(PaymentPipelineFactory factory)
    {
        var payment = new Payment { Id = "pay-1", CheckNumber = "EFT0001" };
        factory.Payments.GetByIdAsync("pay-1").Returns(payment);
        factory.Payments.GetByCheckNumberAsync("EFT0001").Returns(payment);
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(_log);
        }));
    }

    private HttpClient As(string subject, string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task Reading_a_payment_by_id_or_check_number_is_logged_against_the_payment()
    {
        Assert.Equal(HttpStatusCode.OK, (await As("fin-1", ChoRolePermissions.Finance).GetAsync("/api/payments/pay-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As("fin-2", ChoRolePermissions.Finance).GetAsync("/api/payments/check/EFT0001")).StatusCode);

        Assert.Contains(_log.All, e => e.ActorId == "fin-1" && e.RecordType == "Payment" && e.RecordId == "pay-1" && e.Action == "View" && e.TenantId == Tenant);
        Assert.Contains(_log.All, e => e.ActorId == "fin-2" && e.RecordId == "pay-1" && e.Action == "View");
    }

    [Fact]
    public async Task A_role_without_payments_read_is_refused_and_logged()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await As("ex-1", ChoRolePermissions.ClaimsExaminer).GetAsync("/api/payments/pay-1")).StatusCode);

        Assert.Contains(_log.All, e => e.ActorId == "ex-1" && e.RecordId == "pay-1" && e.Outcome == RecordAccessOutcomes.Denied);
    }

    [Fact]
    public async Task The_payment_access_log_is_for_audit_readers_and_is_itself_logged()
    {
        await As("fin-3", ChoRolePermissions.Finance).GetAsync("/api/payments/pay-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await As("fin-3", ChoRolePermissions.Finance).GetAsync("/api/payments/pay-1/access-log")).StatusCode);

        var response = await As("co-1", ChoRolePermissions.ComplianceOfficer).GetAsync("/api/payments/pay-1/access-log");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains((await response.Content.ReadFromJsonAsync<List<RecordAccessEvent>>())!, e => e.ActorId == "fin-3");
        Assert.Contains(_log.All, e => e.ActorId == "co-1" && e.Action == RecordAccessActions.AccessLog);
    }
}
