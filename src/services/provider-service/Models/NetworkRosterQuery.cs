using System.ComponentModel.DataAnnotations;

namespace ProviderService.Models;

/// <summary>
/// Filters, paging, and sort applied to <c>GET /api/v1/networks/{id}/roster</c>.
///
/// <para>
/// Bound from the query string by ASP.NET model binding. The route's
/// <c>{id}</c> populates <see cref="NetworkId"/>; remaining fields come
/// from <c>?lineOfBusiness=...&amp;specialty=...</c>. <see cref="TenantId"/>
/// is set by the controller from <c>HttpContext.Items["TenantId"]</c>
/// — never trusted from the wire.
/// </para>
/// </summary>
public sealed class NetworkRosterQuery
{
    /// <summary>Tenant scope. Set by the controller, not by the caller.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Network chain key (<see cref="Organization.OrganizationId"/>) the
    /// roster is being requested for. Set from the route, not the query
    /// string.
    /// </summary>
    public string NetworkId { get; set; } = string.Empty;

    /// <summary>Optional LOB filter. ANDed with all other filters.</summary>
    public LineOfBusiness? LineOfBusiness { get; set; }

    /// <summary>
    /// NUCC taxonomy code or specialty substring. Matches case-insensitively
    /// against <see cref="Provider.PrimarySpecialty"/> and
    /// <see cref="Provider.TaxonomyCode"/>.
    /// </summary>
    [StringLength(50)]
    public string? Specialty { get; set; }

    /// <summary>Network tier exact match (e.g. <c>Tier1</c>).</summary>
    [StringLength(20)]
    public string? Tier { get; set; }

    /// <summary>When set, restricts to participations matching the flag.</summary>
    public bool? AcceptingNewPatients { get; set; }

    /// <summary>
    /// Snapshot date. When supplied, the roster includes only providers
    /// whose latest Active version is in effect on this date AND whose
    /// matching <see cref="NetworkParticipation"/> has
    /// <c>EffectiveDate &lt;= AsOfDate</c> AND
    /// <c>(TerminationDate is null OR TerminationDate &gt;= AsOfDate)</c>.
    /// Defaults to <see cref="DateTime.UtcNow"/> when null.
    /// </summary>
    public DateTime? AsOfDate { get; set; }

    /// <summary>1-based page index. Used only when <see cref="Cursor"/> is null.</summary>
    [Range(1, int.MaxValue)]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size. Defaults to 100 (set in <c>NetworkRosterDefaults</c>);
    /// hard cap of 1000 enforced by the controller via <c>Math.Clamp</c>.
    /// </summary>
    [Range(1, 1000)]
    public int PageSize { get; set; } = NetworkRosterDefaults.DefaultPageSize;

    /// <summary>
    /// Sort selector. Accepts <c>name</c> (default) or <c>integrityScore</c>.
    /// <c>distance</c> is reserved but not yet supported — the controller
    /// returns 400 with a "not yet supported" message; see
    /// <c>docs/architecture/network-roster-api.md</c> for the planned
    /// geospatial-index treatment.
    /// </summary>
    [StringLength(32)]
    public string? SortBy { get; set; }

    /// <summary>Sort direction. <c>asc</c> or <c>desc</c>. Default depends on <see cref="SortBy"/>.</summary>
    [StringLength(8)]
    public string? SortDirection { get; set; }

    /// <summary>
    /// Opaque pagination cursor. When supplied, overrides
    /// <see cref="Page"/>. The cursor is bound to the rest of the query
    /// via a filter hash; mismatched filters → 400.
    /// </summary>
    [StringLength(2048)]
    public string? Cursor { get; set; }
}

/// <summary>
/// Defaults and limits for the network roster endpoint, kept in one place
/// so controller, service, and tests share them.
/// </summary>
public static class NetworkRosterDefaults
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 1000;
    public const string SortByName = "name";
    public const string SortByIntegrityScore = "integrityScore";
    public const string SortByDistance = "distance";
    public const string DirectionAsc = "asc";
    public const string DirectionDesc = "desc";
}

/// <summary>
/// Resolved sort selector after defaulting + validation. Kept separate
/// from the wire-shape <see cref="NetworkRosterQuery"/> so the service
/// layer never has to re-parse strings.
/// </summary>
public enum NetworkRosterSort
{
    NameAsc,
    NameDesc,
    IntegrityScoreDesc,
}
