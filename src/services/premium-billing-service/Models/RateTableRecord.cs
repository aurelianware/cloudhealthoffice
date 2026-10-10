using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PremiumBillingService.Rating;

namespace PremiumBillingService.Models;

/// <summary>
/// One stored version of a rate table. Versions are immutable: a correction,
/// a new period end or a withdrawal is a new version with the next number,
/// so an invoice line that records <c>(RateTableId, Version)</c> can always be
/// re-rated with exactly the rates it was billed with.
/// </summary>
public class RateTableRecord
{
    /// <summary>Document id: derived from tenant, rate table id and version (see <see cref="DocumentId"/>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string RateTableId { get; set; } = string.Empty;

    public int Version { get; set; }

    public string PlanId { get; set; } = string.Empty;

    /// <summary>A withdrawn table no longer rates anything (from this version on).</summary>
    public bool Withdrawn { get; set; }

    public string? ChangeReason { get; set; }

    /// <summary>SHA-256 (hex) of the table's rating content (<see cref="HashOf"/>).</summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public RateTable Table { get; set; } = new();

    /// <summary>
    /// Unique per tenant, rate table and version, and safe as a Cosmos id and a
    /// Mongo _id (one Mongo collection holds every tenant). Creating a version
    /// that exists fails, so two writers cannot both create version N.
    /// </summary>
    public static string DocumentId(string tenantId, string rateTableId, int version)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{rateTableId}")))[..32].ToLowerInvariant();
        return $"rt-{key}-v{version}";
    }

    private static readonly JsonSerializerOptions HashOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Hash of what rates: everything on the table except its id, version and
    /// hash. Computed once when the version is stored and copied onto the lines
    /// rated with it.
    /// </summary>
    public static string HashOf(RateTable table)
    {
        var copy = JsonSerializer.Deserialize<RateTable>(JsonSerializer.Serialize(table, HashOptions), HashOptions)!;
        copy.Id = string.Empty;
        copy.Version = 0;
        copy.ContentHash = null;
        copy.TenantId = string.Empty;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(copy, HashOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>The table as the rating engine uses it, carrying its version and hash.</summary>
    public RateTable ToRatingTable()
    {
        Table.Id = RateTableId;
        Table.Version = Version;
        Table.ContentHash = ContentHash;
        Table.TenantId = TenantId;
        return Table;
    }

    /// <summary>The latest version of each rate table, without withdrawn ones.</summary>
    public static List<RateTableRecord> Current(IEnumerable<RateTableRecord> versions) =>
        versions.GroupBy(v => v.RateTableId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(v => v.Version).First())
            .Where(v => !v.Withdrawn)
            .ToList();
}

/// <summary>Request to create a rate table or a new version of one.</summary>
public class SaveRateTableRequest
{
    /// <summary>The rates. Its <c>Id</c> names the rate table; a new id starts at version 1.</summary>
    public RateTable Table { get; set; } = new();

    /// <summary>Why this version was made (shown in the version history).</summary>
    public string? ChangeReason { get; set; }

    /// <summary>
    /// The version this one replaces (optimistic check): null for a new table,
    /// otherwise the latest version the caller saw.
    /// </summary>
    public int? ExpectedCurrentVersion { get; set; }
}

public class WithdrawRateTableRequest
{
    public string Reason { get; set; } = string.Empty;

    public int? ExpectedCurrentVersion { get; set; }
}
