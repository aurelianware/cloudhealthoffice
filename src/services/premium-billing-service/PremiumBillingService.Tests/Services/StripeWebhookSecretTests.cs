using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Services;
using Stripe;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// The Stripe webhook is anonymous and its signature is its only
/// authentication. With no secret configured, an event "signed" with the empty
/// key must not be accepted (it would settle any tenant's draft named in its
/// metadata), and a deployment with Stripe configured must not start without one.
/// </summary>
public class StripeWebhookSecretTests
{
    private static StripeAchService Service(string? webhookSecret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:SecretKey"] = "sk_test_fake",
                ["Stripe:WebhookSecret"] = webhookSecret,
            })
            .Build();
        return new StripeAchService(configuration, Mock.Of<ICurrentActor>(), Mock.Of<ILogger<StripeAchService>>());
    }

    /// <summary>A payment_intent.succeeded event naming another tenant, signed with <paramref name="key"/>.</summary>
    private static (string Json, string Signature) ForgedEvent(string key)
    {
        var json = $$"""
            {
              "id": "evt_forged",
              "object": "event",
              "api_version": "{{StripeConfiguration.ApiVersion}}",
              "created": 1700000000,
              "livemode": false,
              "pending_webhooks": 1,
              "request": { "id": null, "idempotency_key": null },
              "type": "payment_intent.succeeded",
              "data": { "object": {
                "id": "pi_victim", "object": "payment_intent", "amount": 150000,
                "metadata": { "invoice_number": "INV-1", "tenant_id": "victim-tenant" } } }
            }
            """;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{json}"))).ToLowerInvariant();
        return (json, $"t={timestamp},v1={signature}");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("whsec_...")]
    public async Task EventSignedWithTheEmptyKey_IsRejected_WhenNoSecretIsConfigured(string? configured)
    {
        var (json, signature) = ForgedEvent(string.Empty);

        var act = () => Service(configured).ProcessWebhookAsync(json, signature);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*webhook secret is not configured*");
    }

    [Fact]
    public async Task EventSignedWithTheConfiguredSecret_IsAccepted()
    {
        var (json, signature) = ForgedEvent("whsec_real_secret");

        var result = await Service("whsec_real_secret").ProcessWebhookAsync(json, signature);

        result.Handled.Should().BeTrue();
        result.TenantId.Should().Be("victim-tenant");
    }

    private sealed class Env : IHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "premium-billing-service";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string? secretKey, string? webhookSecret) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"] = secretKey,
            ["Stripe:WebhookSecret"] = webhookSecret,
        })
        .Build();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Startup_OutsideDevelopment_WithStripeConfiguredAndNoWebhookSecret_Fails(string? webhookSecret)
    {
        var act = () => StripeWebhookSecret.EnsureConfigured(Config("sk_live_x", webhookSecret), new Env("Production"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Stripe:WebhookSecret*");
    }

    [Theory]
    [InlineData("Production", "", "")]              // Stripe not used
    [InlineData("Production", "sk_live_x", "whsec_x")]
    [InlineData("Development", "sk_test_x", "")]
    [InlineData("Testing", "sk_test_x", "")]
    public void Startup_IsAllowed(string environment, string secretKey, string webhookSecret)
    {
        var act = () => StripeWebhookSecret.EnsureConfigured(Config(secretKey, webhookSecret), new Env(environment));

        act.Should().NotThrow();
    }
}
