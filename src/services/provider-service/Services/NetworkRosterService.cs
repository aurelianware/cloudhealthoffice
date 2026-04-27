using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services;

/// <summary>
/// Owns the read path for <c>GET /api/v1/networks/{id}/roster</c>:
/// resolves filter + sort, drives the repository, decodes/encodes the
/// pagination cursor, and maps results into <see cref="NetworkRosterEntry"/>.
///
/// <para>
/// Reads from the cached <see cref="Provider.IntegrityScore"/> column —
/// never calls into <c>ProviderVerificationOrchestrator</c>. See
/// <c>docs/architecture/network-roster-api.md</c> for the read-path
/// contract and the known gap around verification write-back.
/// </para>
/// </summary>
public interface INetworkRosterService
{
    Task<NetworkRosterResponse> GetRosterAsync(NetworkRosterQuery query, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="NetworkRosterService"/> for client-correctable
/// problems. The controller maps each <see cref="ErrorCode"/> to an
/// appropriate HTTP status — currently 400 for everything.
/// </summary>
public sealed class NetworkRosterValidationException : Exception
{
    public string ErrorCode { get; }

    public NetworkRosterValidationException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public class NetworkRosterService : INetworkRosterService
{
    private readonly IProviderRepository _repository;
    private readonly ILogger<NetworkRosterService> _logger;

    public NetworkRosterService(
        IProviderRepository repository,
        ILogger<NetworkRosterService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<NetworkRosterResponse> GetRosterAsync(NetworkRosterQuery query, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (string.IsNullOrEmpty(query.TenantId))
            throw new ArgumentException("Query must have TenantId set by the controller.", nameof(query));
        if (string.IsNullOrEmpty(query.NetworkId))
            throw new NetworkRosterValidationException("missing_network_id", "NetworkId is required.");

        // Defaults are stable: AsOfDate defaults to UTC now, and we serialize
        // it into the cursor so re-paging stays consistent across pages even
        // if the clock advances mid-traversal.
        if (query.AsOfDate == null) query.AsOfDate = DateTime.UtcNow;
        query.PageSize = Math.Clamp(
            query.PageSize <= 0 ? NetworkRosterDefaults.DefaultPageSize : query.PageSize,
            1,
            NetworkRosterDefaults.MaxPageSize);

        var sort = ResolveSort(query);

        // Cursor wins over Page when both are supplied. Filter-hash binding
        // means tampering with filters between pages is rejected — no
        // half-page splices.
        var skip = 0;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            var decoded = NetworkRosterCursor.Decode(query.Cursor);
            var expectedHash = ComputeFilterHash(query, sort);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(decoded.FilterHash),
                    Encoding.UTF8.GetBytes(expectedHash)))
            {
                throw new NetworkRosterValidationException(
                    "cursor_filter_mismatch",
                    "Cursor filter hash does not match the supplied query. Restart pagination from page 1.");
            }
            skip = decoded.Offset;
            // Lock AsOfDate to the cursor so re-paging doesn't drift.
            query.AsOfDate = decoded.AsOfDate;
        }
        else if (query.Page > 1)
        {
            skip = (query.Page - 1) * query.PageSize;
        }

        var rows = await _repository.ListNetworkRosterAsync(query, sort, skip, ct);

        if (sort == NetworkRosterSort.IntegrityScoreDesc)
        {
            rows = ApplyNullsLastForIntegrityScore(rows);
        }

        var items = rows.Select(p => MapToEntry(p, query)).ToList();

        // We requested PageSize rows. A short page means the underlying
        // result set is exhausted and there's no next cursor. We don't
        // peek ahead — saves one trip and keeps the cursor stateless.
        string? nextCursor = null;
        if (items.Count == query.PageSize)
        {
            nextCursor = NetworkRosterCursor.Encode(new NetworkRosterCursor
            {
                Offset = skip + query.PageSize,
                AsOfDate = query.AsOfDate!.Value,
                FilterHash = ComputeFilterHash(query, sort),
            });
        }

        return new NetworkRosterResponse
        {
            Items = items,
            NextCursor = nextCursor,
            PageSize = query.PageSize,
        };
    }

    internal static NetworkRosterSort ResolveSort(NetworkRosterQuery query)
    {
        var sortBy = (query.SortBy ?? NetworkRosterDefaults.SortByName).Trim();
        var direction = (query.SortDirection ?? string.Empty).Trim();

        if (sortBy.Equals(NetworkRosterDefaults.SortByDistance, StringComparison.OrdinalIgnoreCase))
        {
            // §4b — deferred. The roster API rejects distance sort with a
            // 400 + "not yet supported" until the geospatial-index follow-up
            // ships. See network-roster-api.md "Deferred — distance sort".
            throw new NetworkRosterValidationException(
                "distance_sort_unsupported",
                "sortBy=distance is not yet supported. Tracked in network-roster-api.md follow-up.");
        }

        if (sortBy.Equals(NetworkRosterDefaults.SortByIntegrityScore, StringComparison.OrdinalIgnoreCase))
        {
            // integrityScore only sorts descending. ascending makes no
            // operational sense (we don't surface "worst providers first")
            // so we fold any explicit asc into desc and document it.
            return NetworkRosterSort.IntegrityScoreDesc;
        }

        if (sortBy.Equals(NetworkRosterDefaults.SortByName, StringComparison.OrdinalIgnoreCase))
        {
            return direction.Equals(NetworkRosterDefaults.DirectionDesc, StringComparison.OrdinalIgnoreCase)
                ? NetworkRosterSort.NameDesc
                : NetworkRosterSort.NameAsc;
        }

        throw new NetworkRosterValidationException(
            "unsupported_sort",
            $"sortBy '{query.SortBy}' is not supported. Allowed: name | integrityScore.");
    }

    /// <summary>
    /// Reorders so providers without an integrity score trail those with
    /// one. The repository layers can't express this uniformly across
    /// Cosmos and Mongo (Cosmos: IS_DEFINED prefix; Mongo: nulls-first on
    /// descending), so we normalize after the fact on the page-sized
    /// slice.
    /// </summary>
    internal static IReadOnlyList<Provider> ApplyNullsLastForIntegrityScore(IReadOnlyList<Provider> rows)
    {
        return rows
            .OrderByDescending(p => p.IntegrityScore.HasValue)
            .ThenByDescending(p => p.IntegrityScore ?? int.MinValue)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Picks the participation row that drove the match. The repository
    /// ElemMatch ensures at least one participation under
    /// <see cref="NetworkRosterQuery.NetworkId"/> satisfied every filter;
    /// we re-walk the same predicate here so the response surfaces that
    /// row rather than an arbitrary first-with-NetworkId.
    /// </summary>
    private NetworkRosterEntry MapToEntry(Provider provider, NetworkRosterQuery query)
    {
        var asOf = (query.AsOfDate ?? DateTime.UtcNow).ToUniversalTime();

        bool ParticipationDroveTheMatch(NetworkParticipation n)
        {
            if (n.NetworkId != query.NetworkId) return false;
            if (n.EffectiveDate > asOf) return false;
            if (n.TerminationDate.HasValue && n.TerminationDate.Value < asOf) return false;
            if (query.LineOfBusiness.HasValue && n.LineOfBusiness != query.LineOfBusiness.Value) return false;
            if (!string.IsNullOrEmpty(query.Tier) && !string.Equals(n.NetworkTier, query.Tier, StringComparison.Ordinal)) return false;
            if (query.AcceptingNewPatients.HasValue && n.AcceptingNewPatients != query.AcceptingNewPatients.Value) return false;
            return true;
        }

        var participation = provider.NetworkParticipations.FirstOrDefault(ParticipationDroveTheMatch)
            ?? provider.NetworkParticipations.FirstOrDefault(n => n.NetworkId == query.NetworkId)
            ?? new NetworkParticipation();

        var displayName = provider.ProviderType == ProviderType.Individual
            ? string.Join(' ',
                new[] { provider.FirstName, provider.MiddleName, provider.LastName, provider.Credentials }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
            : (provider.OrganizationName ?? string.Empty);

        var panel = HasAnyPanelGating(participation)
            ? new RosterPanelGating
            {
                PanelLimit = participation.PanelLimit,
                PanelAccepted = participation.PanelAccepted,
                AcceptedLobs = participation.AcceptedLobs.Count > 0 ? participation.AcceptedLobs : null,
                MinAcceptedAgeYears = participation.MinAcceptedAgeYears,
                MaxAcceptedAgeYears = participation.MaxAcceptedAgeYears,
            }
            : null;

        var integrity = provider.IntegrityScore.HasValue
                        || !string.IsNullOrEmpty(provider.IntegrityRating)
                        || provider.LastVerifiedAt.HasValue
            ? new RosterIntegrityScore
            {
                Score = provider.IntegrityScore,
                Rating = provider.IntegrityRating,
                LastVerifiedAt = provider.LastVerifiedAt,
            }
            : null;

        return new NetworkRosterEntry
        {
            ProviderId = provider.ProviderId,
            VersionId = provider.VersionId,
            Provider = new RosterProviderSummary
            {
                Npi = provider.NPI,
                ProviderType = provider.ProviderType,
                DisplayName = displayName,
                PrimarySpecialty = provider.PrimarySpecialty,
                TaxonomyCode = provider.TaxonomyCode,
                Address = provider.Address,
                City = provider.City,
                State = provider.State,
                ZipCode = provider.ZipCode,
                AcceptingNewPatients = provider.AcceptingNewPatients,
            },
            Participation = new RosterParticipationSummary
            {
                PlanId = participation.PlanId,
                LineOfBusiness = participation.LineOfBusiness,
                NetworkTier = participation.NetworkTier,
                AcceptingNewPatients = participation.AcceptingNewPatients,
                EffectiveDate = participation.EffectiveDate,
                TerminationDate = participation.TerminationDate,
                PanelGating = panel,
            },
            IntegrityScore = integrity,
        };
    }

    private static bool HasAnyPanelGating(NetworkParticipation n)
        => n.PanelLimit.HasValue
            || n.PanelAccepted.HasValue
            || n.AcceptedLobs.Count > 0
            || n.MinAcceptedAgeYears.HasValue
            || n.MaxAcceptedAgeYears.HasValue;

    /// <summary>
    /// Stable hash of every field that affects result ordering / membership.
    /// Used to bind a cursor to its query so an attacker (or a buggy
    /// client) can't smuggle a different filter set into a mid-page
    /// continuation. We hash the canonicalized JSON representation so
    /// field order doesn't matter.
    /// </summary>
    internal static string ComputeFilterHash(NetworkRosterQuery query, NetworkRosterSort sort)
    {
        var canonical = new
        {
            t = query.TenantId,
            n = query.NetworkId,
            lob = query.LineOfBusiness?.ToString(),
            sp = query.Specialty,
            ti = query.Tier,
            ap = query.AcceptingNewPatients,
            ps = query.PageSize,
            so = sort.ToString(),
        };
        var json = JsonSerializer.Serialize(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        // First 16 bytes (128 bits) is plenty — we're not authenticating,
        // just preventing accidental reuse with mismatched filters.
        return Convert.ToHexString(bytes, 0, 16);
    }
}

/// <summary>
/// Opaque cursor payload. Encoded as URL-safe base64 of the JSON shape;
/// callers should treat the string as opaque.
/// </summary>
internal sealed class NetworkRosterCursor
{
    public int Offset { get; set; }
    public DateTime AsOfDate { get; set; }
    public string FilterHash { get; set; } = string.Empty;

    public static string Encode(NetworkRosterCursor cursor)
    {
        var json = JsonSerializer.Serialize(cursor);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ToBase64Url(bytes);
    }

    public static NetworkRosterCursor Decode(string token)
    {
        try
        {
            var bytes = FromBase64Url(token);
            var json = Encoding.UTF8.GetString(bytes);
            var decoded = JsonSerializer.Deserialize<NetworkRosterCursor>(json)
                ?? throw new NetworkRosterValidationException("cursor_invalid", "Cursor is empty.");
            if (decoded.Offset < 0)
                throw new NetworkRosterValidationException("cursor_invalid", "Cursor offset must be >= 0.");
            return decoded;
        }
        catch (Exception ex) when (ex is not NetworkRosterValidationException)
        {
            throw new NetworkRosterValidationException("cursor_invalid", "Cursor is not a valid pagination token.");
        }
    }

    // .NET 8 has no built-in url-safe base64; the standard transform is
    // base64 → swap '+/' for '-_' and strip '='.
    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        var s = token.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
