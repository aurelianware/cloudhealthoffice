namespace CapitationService.Services;

/// <summary>
/// The Stripe Connect webhook endpoint is anonymous: the <c>Stripe-Signature</c>
/// HMAC over the body is its only authentication, and the tenant comes from the
/// signed event. An empty secret would let anyone sign events (HMAC with an
/// empty key), so an empty or placeholder secret is never used.
/// </summary>
public static class StripeWebhookSecret
{
    /// <summary>A real secret: not empty and not a published placeholder such as <c>whsec_...</c>.</summary>
    public static bool IsUsable(string? secret)
        => !string.IsNullOrWhiteSpace(secret) && !secret.Contains("...", StringComparison.Ordinal);

    /// <summary>
    /// Refuses to start outside Development/Testing when Stripe is configured
    /// (<c>Stripe:SecretKey</c> set) but neither <c>Stripe:ConnectWebhookSecret</c>
    /// nor <c>Stripe:WebhookSecret</c> is: transfers would be created whose
    /// outcomes could never be accepted. A deployment that does not use Stripe
    /// Connect leaves <c>Stripe:SecretKey</c> empty.
    /// </summary>
    public static void EnsureConfigured(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return;
        if (IsUsable(configuration["Stripe:SecretKey"])
            && !IsUsable(configuration["Stripe:ConnectWebhookSecret"])
            && !IsUsable(configuration["Stripe:WebhookSecret"]))
            throw new InvalidOperationException(
                "Stripe:SecretKey is configured but no Stripe webhook secret is (Stripe:ConnectWebhookSecret or " +
                "Stripe:WebhookSecret). The Stripe webhook is authenticated only by its signature; set the secret " +
                "(Kubernetes secret stripe-api-keys, key webhook-secret) or leave Stripe:SecretKey empty when Stripe " +
                "Connect is not used.");
    }
}
