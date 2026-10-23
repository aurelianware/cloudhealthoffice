using System.Net;
using System.Text;
using CapitationService.Services;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CapitationService.Tests.Services;

/// <summary>
/// Where the maker-checker switch lives
/// (plan-configuration.md §12.5). The rule itself is tested in
/// <c>SeparationOfDutiesTests</c>; what is tested here is which answer the
/// control takes, now that the same question can be answered in two places.
///
/// <para>
/// Plan configuration is the authority wherever a value is set. Where nothing
/// is set the tenant document is still read, because a tenant that turned the
/// rule off before plan configuration existed should not have it switched back
/// on without being told — and that read is the one being retired. In every
/// other case, including an unreachable tenant-service, the rule stays on: a
/// control that an outage can remove is not a control.
/// </para>
/// </summary>
public class PaymentControlSettingTests
{
    private const string Tenant = "tenant-1";
    private const string TurnedOffInTenantDoc =
        """{"tenantId":"tenant-1","configuration":{"paymentControls":{"enforceSeparationOfDuties":false}}}""";

    private static (TenantPaymentControls Controls, InMemoryPlanSettings Settings, Counter Calls) Build(
        string tenantDocument = "{}", HttpStatusCode status = HttpStatusCode.OK)
    {
        var settings = new InMemoryPlanSettings();
        var calls = new Counter();
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(TenantPaymentControls.HttpClientName))
            .Returns(() => new HttpClient(new CountingHandler(status, tenantDocument, calls))
            {
                BaseAddress = new Uri("http://tenant-service"),
            });
        return (new TenantPaymentControls(settings, factory.Object, Mock.Of<ILogger<TenantPaymentControls>>()),
            settings, calls);
    }

    private static void Set(InMemoryPlanSettings settings, bool enforced) => settings.Set(
        Tenant, TenantPaymentControls.SettingKey, SettingLevel.Tenant, string.Empty, enforced ? "true" : "false");

    [Fact]
    public void The_setting_is_in_the_catalog_at_tenant_level_and_defaults_to_enforced()
    {
        var definition = PlanSettingCatalog.Require("payment.enforceSeparationOfDuties");

        definition.Type.Should().Be(SettingType.Boolean);
        definition.Default.Should().Be("true");
        definition.AllowedLevels.Should().Equal(SettingLevel.Tenant);
        // Removing a two-person rule single-handedly would defeat the rule.
        definition.ChangeControl.Should().Be(ChangeControl.TwoPersonApproval);
        TenantPaymentControls.SettingKey.Should().Be(definition.Key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_value_in_plan_configuration_decides_and_the_tenant_document_is_not_read(bool enforced)
    {
        var (controls, settings, calls) = Build(TurnedOffInTenantDoc);
        Set(settings, enforced);

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().Be(enforced);
        // Including the case where the two disagree: the tenant document here
        // says off, and a setting of true still means on.
        calls.Count.Should().Be(0);
    }

    [Fact]
    public async Task With_nothing_set_the_tenant_document_still_turns_the_rule_off()
    {
        var (controls, _, calls) = Build(TurnedOffInTenantDoc);

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().BeFalse();
        calls.Count.Should().Be(1);
    }

    [Fact]
    public async Task With_nothing_set_anywhere_the_rule_is_enforced()
    {
        var (controls, _, _) = Build();

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unreachable_tenant_service_leaves_the_rule_enforced_without_a_second_attempt()
    {
        // The client answers the definition's default and says so. Reading the
        // tenant document instead would need the same service that just failed,
        // and a 404 or an empty body there would read as "no override" — which
        // is exactly the "could not look" / "there is none" confusion the
        // directories are built to avoid.
        var (controls, settings, calls) = Build(TurnedOffInTenantDoc);
        settings.Unavailable = true;

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().BeTrue();
        calls.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_tenant_document_that_cannot_be_read_leaves_the_rule_enforced()
    {
        var (controls, _, _) = Build(TurnedOffInTenantDoc, HttpStatusCode.ServiceUnavailable);

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().BeTrue();
    }

    [Fact]
    public async Task One_tenants_answer_is_not_another_tenants()
    {
        var (controls, settings, _) = Build();
        Set(settings, enforced: false);

        (await controls.IsSeparationOfDutiesEnforcedAsync(Tenant)).Should().BeFalse();
        (await controls.IsSeparationOfDutiesEnforcedAsync("tenant-2")).Should().BeTrue();
    }

    private sealed class Counter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CountingHandler(HttpStatusCode status, string body, Counter calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            calls.Increment();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
