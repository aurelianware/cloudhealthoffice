using System.Net;
using System.Security.Cryptography;
using System.Text;
using Stripe;

namespace CloudHealthOffice.TenantService.Tests.Security;

/// <summary>
/// The Stripe webhook is the one anonymous action in tenant-service. It acts
/// only on a payload signed with the configured endpoint secret, and refuses
/// everything when no real secret is configured.
/// </summary>
public static class StripeWebhook
{
    public const string Path = "/api/v1/billing/webhook";

    public static string Payload(string type = "invoice.payment_failed") =>
        "{\"id\":\"evt_test_1\",\"object\":\"event\",\"api_version\":\"" + StripeConfiguration.ApiVersion + "\"," +
        "\"created\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ",\"livemode\":false,\"pending_webhooks\":1," +
        "\"request\":{\"id\":null,\"idempotency_key\":null},\"type\":\"" + type + "\"," +
        "\"data\":{\"object\":{\"id\":\"in_1\",\"object\":\"invoice\",\"customer\":\"cus_1\"}}}";

    /// <summary>A <c>Stripe-Signature</c> header as Stripe computes it (v1 = HMAC-SHA256 of "t.payload").</summary>
    public static string Sign(string payload, string secret, DateTimeOffset? at = null)
    {
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return $"t={timestamp},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static HttpRequestMessage Request(string payload, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (signature != null) request.Headers.TryAddWithoutValidation("Stripe-Signature", signature);
        return request;
    }
}

public class StripeWebhookTests : IClassFixture<TenantServiceFactory>
{
    private readonly TenantServiceFactory _factory;

    public StripeWebhookTests(TenantServiceFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
    }

    [Fact]
    public async Task ValidSignature_IsAcceptedWithoutAToken_AndHandled()
    {
        var payload = StripeWebhook.Payload();

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhook.Request(payload, StripeWebhook.Sign(payload, TenantServiceFactory.WebhookSecret)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Stripe.Invocations.Should().ContainSingle();
    }

    [Fact]
    public async Task WrongSecret_IsRefused()
    {
        var payload = StripeWebhook.Payload("customer.subscription.deleted");

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhook.Request(payload, StripeWebhook.Sign(payload, "whsec_attacker")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task TamperedPayload_IsRefused()
    {
        var signed = StripeWebhook.Payload();
        var signature = StripeWebhook.Sign(signed, TenantServiceFactory.WebhookSecret);
        var tampered = signed.Replace("invoice.payment_failed", "customer.subscription.deleted");

        var response = await _factory.CreateClient().SendAsync(StripeWebhook.Request(tampered, signature));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleSignature_IsRefused()
    {
        var payload = StripeWebhook.Payload();
        var signature = StripeWebhook.Sign(payload, TenantServiceFactory.WebhookSecret, DateTimeOffset.UtcNow.AddHours(-1));

        var response = await _factory.CreateClient().SendAsync(StripeWebhook.Request(payload, signature));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task SignedButUnreadableEvent_IsRefusedNot500()
    {
        const string payload = "{\"id\":\"evt_1\",\"object\":\"event\"}";

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhook.Request(payload, StripeWebhook.Sign(payload, TenantServiceFactory.WebhookSecret)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSignature_IsRefused()
    {
        var response = await _factory.CreateClient().SendAsync(StripeWebhook.Request(StripeWebhook.Payload(), null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }
}

public class StripeWebhookWithoutSecretTests : IClassFixture<TenantServiceWithoutWebhookSecretFactory>
{
    private readonly TenantServiceWithoutWebhookSecretFactory _factory;

    public StripeWebhookWithoutSecretTests(TenantServiceWithoutWebhookSecretFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
    }

    [Theory]
    [InlineData("")]
    [InlineData("whsec_...")]
    [InlineData("whsec_attacker")]
    public async Task NoSecretConfigured_RefusesEverything(string signingSecret)
    {
        var payload = StripeWebhook.Payload("customer.subscription.deleted");

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhook.Request(payload, StripeWebhook.Sign(payload, signingSecret)));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }
}

public class StripeWebhookWithPlaceholderSecretTests : IClassFixture<TenantServiceWithPlaceholderWebhookSecretFactory>
{
    private readonly TenantServiceWithPlaceholderWebhookSecretFactory _factory;

    public StripeWebhookWithPlaceholderSecretTests(TenantServiceWithPlaceholderWebhookSecretFactory factory)
    {
        _factory = factory;
        _factory.ResetMocks();
    }

    [Fact]
    public async Task PublishedPlaceholder_IsNotASecret()
    {
        // appsettings.json used to ship "whsec_..."; anyone could sign with it.
        var payload = StripeWebhook.Payload("customer.subscription.deleted");

        var response = await _factory.CreateClient().SendAsync(
            StripeWebhook.Request(payload, StripeWebhook.Sign(payload, TenantServiceWithPlaceholderWebhookSecretFactory.Placeholder)));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.Stripe.Invocations.Should().BeEmpty();
    }
}
