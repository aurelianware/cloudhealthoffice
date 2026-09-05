using System.Collections.Concurrent;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>
/// State store for outbound Payer-to-Payer exchanges. The exchange record — not
/// a transient variable inside a controller — is what the workflow advances, so
/// an initiation that is retried resumes the same exchange and an operator can
/// see how any exchange ended.
///
/// <see cref="ReserveAsync"/> is the idempotency primitive: it either creates the
/// exchange or hands back the one already registered under the same key
/// (tenant | member | target payer | transition), atomically, so two concurrent
/// initiations cannot open two exchanges for one coverage transition.
/// </summary>
public interface IPayerToPayerOutboundExchangeStore
{
    /// <summary>
    /// Registers <paramref name="exchange"/> under its idempotency key, or
    /// returns the existing exchange for that key. The bool is true when the
    /// passed exchange was the one registered (a fresh initiation).
    /// </summary>
    Task<(PayerToPayerOutboundExchange Exchange, bool IsNew)> ReserveAsync(
        PayerToPayerOutboundExchange exchange, CancellationToken ct = default);

    /// <summary>Persists the current state of an exchange.</summary>
    Task SaveAsync(PayerToPayerOutboundExchange exchange, CancellationToken ct = default);

    /// <summary>Reads an exchange by id within a tenant; null when it is not this tenant's.</summary>
    Task<PayerToPayerOutboundExchange?> GetAsync(
        string tenantId, string exchangeId, CancellationToken ct = default);
}

/// <summary>
/// In-memory exchange store, mirroring the in-process store idiom the rest of
/// fhir-service uses in Demo mode (DtrService's questionnaires,
/// CrdClassificationStore). It is tenant-scoped and safe for concurrent use.
///
/// LIMITATION (documented, not hidden): exchange state lives in the process, so
/// it does not survive a restart and is not shared across instances. Binding this
/// interface to the platform's durable store (the pattern
/// <c>IAuthorizationRepository</c> follows in authorization-service) is the
/// production wiring; the workflow above it is unchanged by that swap.
/// </summary>
public sealed class InMemoryPayerToPayerOutboundExchangeStore : IPayerToPayerOutboundExchangeStore
{
    private readonly ConcurrentDictionary<string, PayerToPayerOutboundExchange> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PayerToPayerOutboundExchange> _byId = new(StringComparer.Ordinal);

    public Task<(PayerToPayerOutboundExchange Exchange, bool IsNew)> ReserveAsync(
        PayerToPayerOutboundExchange exchange, CancellationToken ct = default)
    {
        var key = Scoped(exchange.TenantId, exchange.IdempotencyKey);
        var registered = _byIdempotencyKey.GetOrAdd(key, exchange);
        var isNew = ReferenceEquals(registered, exchange);
        if (isNew) _byId[Scoped(exchange.TenantId, exchange.ExchangeId)] = exchange;
        return Task.FromResult((registered, isNew));
    }

    public Task SaveAsync(PayerToPayerOutboundExchange exchange, CancellationToken ct = default)
    {
        exchange.UpdatedAtUtc = DateTime.UtcNow;
        _byId[Scoped(exchange.TenantId, exchange.ExchangeId)] = exchange;
        _byIdempotencyKey[Scoped(exchange.TenantId, exchange.IdempotencyKey)] = exchange;
        return Task.CompletedTask;
    }

    public Task<PayerToPayerOutboundExchange?> GetAsync(
        string tenantId, string exchangeId, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(Scoped(tenantId, exchangeId), out var exchange) ? exchange : null);

    // Every key is tenant-prefixed so one tenant's exchange can never be read or
    // replayed from another tenant's request.
    private static string Scoped(string tenantId, string key) => $"{tenantId}|{key}";
}
