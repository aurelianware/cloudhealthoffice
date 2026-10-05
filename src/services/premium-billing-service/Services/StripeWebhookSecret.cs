namespace PremiumBillingService.Services;

/// <summary>
/// The Stripe webhook endpoint is anonymous: the <c>Stripe-Signature</c> HMAC
/// over the body is its only authentication, and the tenant comes from the
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
    /// (<c>Stripe:SecretKey</c> set) but <c>Stripe:WebhookSecret</c> is not: Stripe
    /// would be creating drafts whose outcomes could never be accepted. A
    /// deployment that does not use Stripe ACH leaves <c>Stripe:SecretKey</c> empty.
    /// </summary>
    public static void EnsureConfigured(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            return;
        if (IsUsable(configuration["Stripe:SecretKey"]) && !IsUsable(configuration["Stripe:WebhookSecret"]))
            throw new InvalidOperationException(
                "Stripe:SecretKey is configured but Stripe:WebhookSecret is not. The Stripe webhook is authenticated only by " +
                "its signature; set Stripe:WebhookSecret (Kubernetes secret stripe-api-keys, key webhook-secret) or leave " +
                "Stripe:SecretKey empty when Stripe ACH is not used.");
    }
}
