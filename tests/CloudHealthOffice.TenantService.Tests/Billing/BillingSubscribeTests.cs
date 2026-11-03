using System.Text.Json;
using CloudHealthOffice.TenantService.Tests.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TenantService.Controllers;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace CloudHealthOffice.TenantService.Tests.Billing;

/// <summary>
/// Subscribing a tenant: the Stripe customer and subscription ids are saved on
/// the tenant (so a second subscribe reuses the customer, and the invoice,
/// cancel and tier endpoints can find them), and the subscription carries the
/// tenant id its webhooks look the tenant up by.
/// </summary>
public class BillingSubscribeTests
{
    private const string Tenant = "acme";

    /// <summary>Stores copies, as Mongo does, so an unsaved in-memory change is visible as lost.</summary>
    private sealed class CopyingTenantRepository : ITenantRepository
    {
        private readonly Dictionary<string, string> _docs = new();
        public int ConflictsToThrow { get; set; }

        public void Seed(Tenant tenant) => _docs[tenant.TenantId] = JsonSerializer.Serialize(tenant);
        public Tenant? Stored(string id) => _docs.TryGetValue(id, out var json) ? JsonSerializer.Deserialize<Tenant>(json) : null;

        public Task<Tenant?> GetByTenantIdAsync(string tenantId) => Task.FromResult(Stored(tenantId));
        public Task<Tenant?> GetByIdAsync(string tenantId) => GetByTenantIdAsync(tenantId);

        public Task<Tenant> UpdateAsync(Tenant tenant)
        {
            if (ConflictsToThrow > 0)
            {
                ConflictsToThrow--;
                throw new TenantChangedConcurrentlyException(tenant.TenantId, tenant.Revision);
            }
            Seed(tenant);
            return Task.FromResult(Stored(tenant.TenantId)!);
        }

        public Task<IEnumerable<Tenant>> GetAllAsync(int pageSize = 100, string? continuationToken = null) =>
            Task.FromResult(_docs.Keys.Select(k => Stored(k)!));
        public Task<IEnumerable<Tenant>> GetByStatusAsync(string status) => throw new NotSupportedException();
        public Task<Tenant> CreateAsync(Tenant tenant) => throw new NotSupportedException();
        public Task DeleteAsync(string tenantId) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string tenantId) => Task.FromResult(_docs.ContainsKey(tenantId));
        public Task<Tenant?> GetByApiKeyHashAsync(string keyHash) => Task.FromResult<Tenant?>(null);
        public Task<bool> TryActivatePendingAsync(string tenantId, string actor) => throw new NotSupportedException();
    }

    private readonly CopyingTenantRepository _tenants = new();
    private readonly Mock<IStripeService> _stripe = new(MockBehavior.Strict);
    private readonly TestActor _actor = new("admin", Tenant, "TenantAdmin");

    public BillingSubscribeTests()
    {
        _tenants.Seed(new Tenant
        {
            TenantId = Tenant, TenantName = "Acme", OrganizationName = "Acme Health",
            ContactInfo = new ContactInfo { Email = "billing@acme.test" },
        });
    }

    private TenantManagementService TenantService() =>
        new(_tenants, NullLogger<TenantManagementService>.Instance, Mock.Of<ISftpProvisioningService>(), _actor);

    private BillingController Controller() => new(
        _stripe.Object, TenantService(),
        new StripeWebhookVerifier(new ConfigurationBuilder().Build()),
        new TenantAuditLog(NullLoggerFactory.Instance, _actor),
        NullLogger<BillingController>.Instance);

    [Fact]
    public async Task Subscribing_saves_the_customer_and_subscription_ids_and_tags_the_subscription_with_the_tenant()
    {
        _stripe.Setup(s => s.CreateCustomerAsync(It.Is<Tenant>(t => t.TenantId == Tenant))).ReturnsAsync("cus_1");
        _stripe.Setup(s => s.CreateSubscriptionAsync("cus_1", "starter", Tenant)).ReturnsAsync("sub_1");

        var result = await Controller().CreateSubscription(Tenant);

        result.Should().BeOfType<OkObjectResult>();
        var billing = _tenants.Stored(Tenant)!.Billing!;
        billing.StripeCustomerId.Should().Be("cus_1");
        billing.StripeSubscriptionId.Should().Be("sub_1");
        billing.BillingEmail.Should().Be("billing@acme.test");
        _stripe.VerifyAll();
    }

    [Fact]
    public async Task A_second_subscribe_reuses_the_saved_customer()
    {
        _stripe.Setup(s => s.CreateCustomerAsync(It.IsAny<Tenant>())).ReturnsAsync("cus_1");
        _stripe.Setup(s => s.CreateSubscriptionAsync("cus_1", It.IsAny<string>(), Tenant)).ReturnsAsync("sub_1");

        await Controller().CreateSubscription(Tenant);
        await Controller().CreateSubscription(Tenant, "professional");

        _stripe.Verify(s => s.CreateCustomerAsync(It.IsAny<Tenant>()), Times.Once);
    }

    [Fact]
    public async Task The_customer_id_is_saved_even_when_creating_the_subscription_fails()
    {
        _stripe.Setup(s => s.CreateCustomerAsync(It.IsAny<Tenant>())).ReturnsAsync("cus_1");
        _stripe.Setup(s => s.CreateSubscriptionAsync("cus_1", "starter", Tenant))
            .ThrowsAsync(new InvalidOperationException("Starter pricing ID not configured"));

        var act = () => Controller().CreateSubscription(Tenant);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _tenants.Stored(Tenant)!.Billing!.StripeCustomerId.Should().Be("cus_1");
    }

    [Fact]
    public async Task Saved_ids_let_the_upcoming_invoice_and_cancel_endpoints_find_the_subscription()
    {
        _stripe.Setup(s => s.CreateCustomerAsync(It.IsAny<Tenant>())).ReturnsAsync("cus_1");
        _stripe.Setup(s => s.CreateSubscriptionAsync("cus_1", "starter", Tenant)).ReturnsAsync("sub_1");
        _stripe.Setup(s => s.GetUpcomingInvoiceAsync("cus_1")).ReturnsAsync((Stripe.Invoice?)null);
        _stripe.Setup(s => s.CancelSubscriptionAsync("sub_1")).Returns(Task.CompletedTask);
        await Controller().CreateSubscription(Tenant);

        (await Controller().GetUpcomingInvoice(Tenant)).Should().BeOfType<OkObjectResult>();
        (await Controller().CancelSubscription(Tenant)).Should().BeOfType<OkObjectResult>();
        _tenants.Stored(Tenant)!.Status.Should().Be("suspended");
    }

    [Fact]
    public async Task An_unknown_tenant_is_404_and_stripe_is_not_called()
    {
        var result = await Controller().CreateSubscription("nobody");

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Saving_stripe_ids_survives_a_concurrent_edit()
    {
        _tenants.ConflictsToThrow = 2;

        var saved = await TenantService().RecordStripeBillingAsync(Tenant, "cus_1", "sub_1");

        saved.Billing!.StripeSubscriptionId.Should().Be("sub_1");
        _tenants.Stored(Tenant)!.Billing!.StripeCustomerId.Should().Be("cus_1");
    }

    [Fact]
    public async Task Saving_the_customer_alone_keeps_an_existing_subscription_id()
    {
        await TenantService().RecordStripeBillingAsync(Tenant, "cus_1", "sub_1");

        await TenantService().RecordStripeBillingAsync(Tenant, "cus_1");

        _tenants.Stored(Tenant)!.Billing!.StripeSubscriptionId.Should().Be("sub_1");
    }

    [Fact]
    public async Task Saving_stripe_ids_for_a_missing_tenant_throws()
    {
        var act = () => TenantService().RecordStripeBillingAsync("nobody", "cus_1");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }
}
