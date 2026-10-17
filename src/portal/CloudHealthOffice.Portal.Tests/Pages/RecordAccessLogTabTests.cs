using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The Access log tab on provider, authorization and payment records: shown
/// only to audit:read holders, and it asks for the log by the record's own id
/// (not the check or auth number in the URL), a provider's also by NPI.
/// </summary>
public class RecordAccessLogTabTests : TestContext
{
    private readonly Mock<IRecordAccessLogService> _logs = new();
    private readonly Mock<IUserContextService> _user = new();
    private static readonly RecordAccessLog OneRead = new(
        [new() { Id = "e1", RecordType = "Payment", RecordId = "pay-1", Action = "View", Outcome = "Allowed", ActorId = "fin-1" }], []);

    public RecordAccessLogTabTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_logs.Object);
        Services.AddSingleton(_user.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(Mock.Of<IClaimsService>());
        Services.AddSingleton(Mock.Of<IMemberService>());
        Services.AddSingleton(Mock.Of<IProviderService>());
        Services.AddScoped<GlobalSearch>();
    }

    private void Grant(params string[] permissions) =>
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => permissions.Contains(p));

    private IRenderedComponent<PaymentRecordView> Payment(string id)
    {
        var payments = new Mock<IPaymentService>();
        var payment = new PaymentView { Id = "pay-1", CheckNumber = "EFT0001", PaymentMethod = "ACH", PayeeName = "Lakeside" };
        payments.Setup(p => p.GetPaymentAsync(It.IsAny<string>())).ReturnsAsync((PaymentView?)null);
        payments.Setup(p => p.GetPaymentAsync("pay-1")).ReturnsAsync(payment);
        payments.Setup(p => p.GetPaymentByCheckNumberAsync("EFT0001")).ReturnsAsync(payment);
        Services.AddSingleton(payments.Object);
        return RenderComponent<PaymentRecordView>(p => p.Add(x => x.PaymentId, id).Add(x => x.Tab, "access-log").Add(x => x.BaseHref, $"/payments/{id}"));
    }

    [Fact]
    public void Payment_access_log_is_hidden_without_audit_read()
    {
        Grant("payments:read");

        var cut = Payment("pay-1");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("EFT EFT0001"));
        cut.FindAll("[data-testid=record-tab-access-log]").Should().BeEmpty();
        _logs.Verify(l => l.GetPaymentAccessLogAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Payment_reached_by_check_number_loads_the_log_by_payment_id()
    {
        Grant("payments:read", "audit:read");
        _logs.Setup(l => l.GetPaymentAccessLogAsync("pay-1", It.IsAny<int>())).ReturnsAsync(OneRead);

        var cut = Payment("EFT0001");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=access-log-row]").Should().ContainSingle());
        cut.Find("[data-testid=record-tab-access-log]").GetAttribute("href").Should().Be("/payments/EFT0001/access-log");
    }

    [Fact]
    public void Authorization_access_log_loads_by_its_id()
    {
        Grant("authorizations:read", "audit:read");
        var auths = new Mock<IAuthorizationService>();
        auths.Setup(a => a.GetAuthorizationByIdAsync("PA-77")).ReturnsAsync(new AuthorizationDetails { Id = "auth-1", AuthorizationNumber = "PA-77", MemberId = "M1" });
        Services.AddSingleton(auths.Object);
        _logs.Setup(l => l.GetAuthorizationAccessLogAsync("auth-1", It.IsAny<int>())).ReturnsAsync(new RecordAccessLog([], ["Authorization Service"]));

        var cut = RenderComponent<AuthorizationRecordView>(p => p.Add(x => x.AuthorizationId, "PA-77").Add(x => x.Tab, "access-log").Add(x => x.BaseHref, "/authorizations/PA-77"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=access-log-partial]").TextContent.Should().Contain("Authorization Service"));
    }

    [Fact]
    public void Provider_access_log_includes_bank_detail_reads_by_npi()
    {
        Grant("providers:read", "audit:read");
        var providers = new Mock<IProviderService>();
        providers.Setup(p => p.GetProviderByIdAsync("P1")).ReturnsAsync(new ProviderDetails { ProviderId = "P1", NPI = "1234567893", Name = "Lakeside Orthopedics" });
        Services.AddSingleton(providers.Object);
        _logs.Setup(l => l.GetProviderAccessLogAsync("P1", "1234567893", It.IsAny<int>())).ReturnsAsync(new RecordAccessLog(
            [new() { Id = "e2", RecordType = "Provider", RecordId = "1234567893", Action = "BankAccount", Outcome = "Allowed", ActorId = "fin-1" }], []));

        var cut = RenderComponent<ProviderRecordView>(p => p.Add(x => x.ProviderId, "P1").Add(x => x.Tab, "access-log").Add(x => x.BaseHref, "/providers/P1"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=access-log-row]").Should().ContainSingle());
        cut.Find("[data-testid=access-log-row]").TextContent.Should().Contain("Read bank details");
    }
}
