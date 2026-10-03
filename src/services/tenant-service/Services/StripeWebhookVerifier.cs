using Stripe;

namespace TenantService.Services;

/// <summary>
/// Verifies that a webhook request was signed by Stripe with this
/// deployment's endpoint secret (<c>Stripe:WebhookSecret</c>, from Key Vault),
/// using Stripe's own <see cref="EventUtility.ConstructEvent(string, string, string, long, bool)"/>
/// (HMAC-SHA256 over the timestamped payload, 5-minute tolerance).
///
/// The webhook endpoint is anonymous, so this signature is its only
/// authentication. With no secret configured (or the placeholder from
/// <c>appsettings.json</c>) nothing is accepted: the endpoint answers 503.
/// </summary>
public sealed class StripeWebhookVerifier
{
    private readonly string? _secret;

    public StripeWebhookVerifier(IConfiguration configuration)
    {
        var secret = configuration["Stripe:WebhookSecret"];
        _secret = IsUsable(secret) ? secret : null;
    }

    /// <summary>Whether a real endpoint secret is configured.</summary>
    public bool IsConfigured => _secret != null;

    /// <summary>
    /// Parses the event after checking its signature.
    /// </summary>
    /// <exception cref="InvalidOperationException">No secret is configured.</exception>
    /// <exception cref="StripeException">The signature is missing, wrong or too old.</exception>
    public Event Verify(string payload, string signatureHeader)
    {
        if (_secret == null)
            throw new InvalidOperationException("Stripe:WebhookSecret is not configured.");
        return EventUtility.ConstructEvent(payload, signatureHeader, _secret);
    }

    /// <summary>A placeholder such as <c>whsec_...</c> is a published value, not a secret.</summary>
    private static bool IsUsable(string? secret)
        => !string.IsNullOrWhiteSpace(secret) && !secret.Contains("...", StringComparison.Ordinal);
}
