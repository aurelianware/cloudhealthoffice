using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.Identity;

/// <summary>
/// Caches each trusted issuer's signing keys, refreshes them when a token
/// arrives signed by a key it has not seen, and keeps doing neither of those
/// things too often.
///
/// Three behaviours have to hold at once, and they pull against each other:
///
///   ROTATION must work unattended. An issuer rotates keys on its own schedule
///   and does not tell CHO; the only signal is a token whose <c>kid</c> is
///   unknown. So an unknown kid triggers a refresh.
///
///   THAT SIGNAL IS ATTACKER-CONTROLLED. Anyone can present a token with a
///   random kid, so "unknown kid triggers a refresh" is also an unauthenticated
///   way to make CHO hammer its IdP. Refreshes are therefore rate-limited per
///   issuer (<see cref="MinRefreshInterval"/>) and single-flighted, so a burst
///   of requests carrying one new kid produces exactly one fetch and the rest
///   wait for its result rather than piling on — no thundering herd, and a
///   forged kid costs at most one fetch per interval.
///
///   AN IdP OUTAGE MUST NOT BE AN OUTAGE HERE. Keys already fetched stay usable
///   while the issuer is unreachable, up to <see cref="MaxStaleAge"/>. That
///   window is bounded on purpose: indefinitely stale trust would keep honouring
///   a key the issuer had revoked. Within it, previously-seen kids keep working
///   and unknown ones fail closed — the outage degrades rotation, never
///   signature validation.
/// </summary>
public sealed class SmartSigningKeyRing
{
    /// <summary>Floor between refreshes for one issuer, however many unknown kids arrive.</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>Routine re-fetch interval while everything is healthy.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    /// <summary>How long cached keys may outlive a failing issuer before they stop being trusted.</summary>
    public static readonly TimeSpan MaxStaleAge = TimeSpan.FromHours(24);

    private readonly IIssuerMetadataFetcher _fetcher;
    private readonly TrustedIssuerRegistry _registry;
    private readonly ILogger<SmartSigningKeyRing> _logger;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, IssuerEntry> _entries;

    public SmartSigningKeyRing(
        IIssuerMetadataFetcher fetcher,
        TrustedIssuerRegistry registry,
        ILogger<SmartSigningKeyRing> logger,
        TimeProvider? timeProvider = null)
    {
        _fetcher = fetcher;
        _registry = registry;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _entries = registry.Issuers.ToDictionary(
            i => i.Issuer, i => new IssuerEntry(i), StringComparer.Ordinal);
    }

    private sealed class IssuerEntry(TrustedIssuerOptions options)
    {
        public TrustedIssuerOptions Options { get; } = options;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IssuerMetadata? Metadata { get; set; }
        public DateTimeOffset LastAttemptUtc { get; set; } = DateTimeOffset.MinValue;
        public string? LastError { get; set; }
    }

    /// <summary>
    /// Signing keys for a token from <paramref name="issuerName"/> carrying
    /// <paramref name="kid"/>. Empty means fail closed — the caller must not
    /// fall back to any other issuer's keys.
    /// </summary>
    public IReadOnlyList<SecurityKey> ResolveKeys(string? issuerName, string? kid)
    {
        if (issuerName == null || !_entries.TryGetValue(issuerName, out var entry))
            return [];

        var now = _time.GetUtcNow();
        var cached = entry.Metadata;

        // Cached keys past the staleness bound are no longer trust material.
        if (cached != null && now - cached.RetrievedAtUtc > MaxStaleAge)
        {
            _logger.LogWarning(
                "Signing keys for issuer {Issuer} exceeded the {Hours}h staleness bound and were dropped.",
                Sanitize(issuerName), MaxStaleAge.TotalHours);
            entry.Metadata = null;
            cached = null;
        }

        var needsRefresh =
            cached == null
            || (kid != null && !cached.KeyIds.Contains(kid))
            || now - cached.RetrievedAtUtc > RefreshInterval;

        if (needsRefresh)
            RefreshBlocking(entry, now);

        return entry.Metadata?.SigningKeys ?? [];
    }

    /// <summary>Fetch this issuer's material now, for startup warm-up and readiness.</summary>
    public async Task<bool> TryPrimeAsync(string issuerName, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(issuerName, out var entry)) return false;

        await entry.Gate.WaitAsync(ct);
        try
        {
            await FetchIntoAsync(entry, _time.GetUtcNow(), ct);
            return entry.Metadata != null;
        }
        finally { entry.Gate.Release(); }
    }

    /// <summary>
    /// Cached metadata for one issuer, or null when nothing has been retrieved.
    /// Used by SMART discovery to advertise the authorization server's REAL
    /// endpoints rather than guessing their paths.
    /// </summary>
    public IssuerMetadata? MetadataFor(string issuerName)
        => _entries.TryGetValue(issuerName, out var entry) ? entry.Metadata : null;

    /// <summary>Current trust state per issuer, for the readiness check. No key material.</summary>
    public IReadOnlyList<IssuerTrustStatus> Status()
        => _entries.Values.Select(e => new IssuerTrustStatus
        {
            Issuer = e.Options.Issuer,
            HasKeys = e.Metadata is { SigningKeys.Count: > 0 },
            KeyCount = e.Metadata?.SigningKeys.Count ?? 0,
            RetrievedAtUtc = e.Metadata?.RetrievedAtUtc,
            IsStale = e.Metadata != null && _time.GetUtcNow() - e.Metadata.RetrievedAtUtc > RefreshInterval,
            LastError = e.LastError,
        }).ToList();

    /// <summary>
    /// Single-flight refresh. The key-resolution callback the JWT handler calls
    /// is synchronous, so this blocks — but only one caller per issuer ever
    /// does the fetch, and the rest return as soon as the winner has published
    /// its result.
    /// </summary>
    private void RefreshBlocking(IssuerEntry entry, DateTimeOffset now)
    {
        // Rate limit read before taking the gate: during a burst most callers
        // return here without queueing at all.
        if (now - entry.LastAttemptUtc < MinRefreshInterval && entry.Metadata != null)
            return;

        if (!entry.Gate.Wait(TimeSpan.FromSeconds(10)))
        {
            // Which of these it is decides whether anything still works, so the
            // line has to say. "Using cached keys" when there are none reads as
            // benign during precisely the outage it is reporting.
            if (entry.Metadata != null)
            {
                _logger.LogWarning(
                    "Timed out waiting for a signing-key refresh of issuer {Issuer}; "
                    + "continuing on cached keys.",
                    Sanitize(entry.Options.Issuer));
            }
            else
            {
                _logger.LogError(
                    "Timed out waiting for a signing-key refresh of issuer {Issuer} and no keys "
                    + "are cached; tokens from this issuer cannot be validated.",
                    Sanitize(entry.Options.Issuer));
            }

            return;
        }

        try
        {
            // Re-check inside the gate: the request that won the race has
            // already refreshed, and this one must not fetch again.
            if (now - entry.LastAttemptUtc < MinRefreshInterval && entry.Metadata != null)
                return;

            FetchIntoAsync(entry, now, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { entry.Gate.Release(); }
    }

    private async Task FetchIntoAsync(IssuerEntry entry, DateTimeOffset now, CancellationToken ct)
    {
        entry.LastAttemptUtc = now;
        try
        {
            var metadata = await _fetcher.FetchAsync(entry.Options, ct);

            // Stamped with the ring's OWN clock, not whatever the fetcher
            // reported. Every cache-window decision below — refresh interval,
            // staleness bound — is measured against this, so it has to come
            // from one clock or the windows are meaningless.
            entry.Metadata = metadata with { RetrievedAtUtc = now };
            entry.LastError = null;

            _logger.LogInformation(
                "Refreshed signing keys for issuer {Issuer}: {KeyCount} key(s).",
                Sanitize(entry.Options.Issuer), metadata.SigningKeys.Count);
        }
        catch (Exception ex)
        {
            // The message, never the response body — a failing IdP's payload is
            // exactly the kind of thing that carries material worth not logging.
            entry.LastError = ex.Message;

            if (entry.Metadata != null)
            {
                _logger.LogWarning(
                    "Signing-key refresh failed for issuer {Issuer}; continuing on cached keys "
                    + "retrieved {Retrieved:o}. Reason: {Reason}",
                    Sanitize(entry.Options.Issuer), entry.Metadata.RetrievedAtUtc, Sanitize(ex.Message));
            }
            else
            {
                _logger.LogError(
                    "Signing-key retrieval failed for issuer {Issuer} with no cached keys; "
                    + "tokens from this issuer cannot be validated. Reason: {Reason}",
                    Sanitize(entry.Options.Issuer), Sanitize(ex.Message));
            }
        }
    }

    private static string Sanitize(string value)
        => value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}

/// <summary>Per-issuer trust state for health reporting. Deliberately carries no keys.</summary>
public sealed record IssuerTrustStatus
{
    public required string Issuer { get; init; }
    public required bool HasKeys { get; init; }
    public required int KeyCount { get; init; }
    public DateTimeOffset? RetrievedAtUtc { get; init; }
    public bool IsStale { get; init; }
    public string? LastError { get; init; }
}
