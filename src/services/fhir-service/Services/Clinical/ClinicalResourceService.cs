using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;

namespace FhirService.Services.Clinical;

/// <summary>What a clinical access attempt resolved to. The caller maps it to HTTP.</summary>
public enum ClinicalAccessOutcome
{
    /// <summary>Authorized, and the resource(s) are returned.</summary>
    Granted,

    /// <summary>
    /// No member could be established for the request, or the member named
    /// disagrees with the one the caller is authorized for. Refused.
    /// </summary>
    NotAuthorized,

    /// <summary>
    /// No such resource FOR THIS MEMBER. Deliberately indistinguishable from
    /// "exists, but belongs to someone else" — see <see cref="ClinicalResourceService"/>.
    /// </summary>
    NotFound,

    /// <summary>The request itself was contradictory (e.g. patient and subject naming different members).</summary>
    InvalidRequest,
}

/// <summary>
/// The already-established facts a clinical read runs under. The service never
/// reads <c>HttpContext</c>: authentication, SMART scope, tenant resolution and
/// (for provider callers) attribution plus Provider Access consent have all
/// happened upstream, and what reaches here is their result.
/// </summary>
public sealed record ClinicalAccessContext
{
    /// <summary>From the authenticated context — never a body, header or query value the caller controls.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The member this caller is authorized to read. For a patient-context token
    /// it is the token's own <c>patient</c> binding; for a provider or backend
    /// token it is the member the Provider Access filter authorized the request
    /// against. Null means the request never established one, which is a refusal.
    /// </summary>
    public string? AuthorizedMemberId { get; init; }

    /// <summary>The caller, from the validated token subject. Audited, never trusted as a member.</summary>
    public string? CallerId { get; init; }

    /// <summary>True when the caller is the member reading their own record.</summary>
    public bool IsPatientContext { get; init; }
}

/// <summary>Outcome of a single-resource read.</summary>
public sealed record ClinicalReadResult(ClinicalAccessOutcome Outcome, Resource? Resource)
{
    public static readonly ClinicalReadResult NotAuthorized = new(ClinicalAccessOutcome.NotAuthorized, null);
    public static readonly ClinicalReadResult NotFound = new(ClinicalAccessOutcome.NotFound, null);
}

/// <summary>Outcome of a member-scoped search.</summary>
public sealed record ClinicalSearchResult(
    ClinicalAccessOutcome Outcome, IReadOnlyList<Resource> Resources, int Total)
{
    public static readonly ClinicalSearchResult NotAuthorized =
        new(ClinicalAccessOutcome.NotAuthorized, [], 0);

    public static readonly ClinicalSearchResult InvalidRequest =
        new(ClinicalAccessOutcome.InvalidRequest, [], 0);
}

/// <summary>
/// Reads USCDI clinical resources for Patient and Provider Access.
///
/// AUTHORIZATION HAPPENS BEFORE PHI IS FETCHED, NOT AFTER. Every store call is
/// keyed on the tenant AND the member the caller is authorized for, so a
/// resource belonging to anyone else is not filtered out of a result — it is
/// never selected. Knowing a resource id is not authority to read it: the id is
/// only ever one component of a query that already names the authorized member.
///
/// REFUSALS ARE UNIFORM. "No such resource", "someone else's resource" and
/// "another tenant's resource" all return the same 404. Distinguishing them is
/// precisely what a caller enumerating members needs, so the distinguishing
/// category is kept in the PHI-free audit line instead.
///
/// AUDIT CARRIES NO CONTENT. Tenant, caller, member, resource type, resource id,
/// outcome, count and instant — no observation values, no diagnoses, no
/// medication names, no free text, no resource bodies, no tokens.
/// </summary>
public interface IClinicalResourceService
{
    Task<ClinicalReadResult> ReadAsync(
        ClinicalAccessContext context, string resourceType, string id, CancellationToken ct = default);

    /// <summary>
    /// Searches one clinical resource type for the authorized member.
    /// <paramref name="requestedMemberId"/> is the member the caller named
    /// through <c>patient</c> or <c>subject</c>, already stripped of any
    /// <c>Patient/</c> prefix; it must agree with the authorized member or the
    /// search is refused.
    /// </summary>
    Task<ClinicalSearchResult> SearchAsync(
        ClinicalAccessContext context,
        string resourceType,
        string? requestedMemberId,
        string? idFilter,
        int page,
        int count,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ClinicalResourceService : IClinicalResourceService
{
    private readonly IClinicalResourceStore _store;
    private readonly ClinicalResourceProjector _projector;
    private readonly ILogger<ClinicalResourceService> _logger;

    public ClinicalResourceService(
        IClinicalResourceStore store,
        ClinicalResourceProjector projector,
        ILogger<ClinicalResourceService> logger)
    {
        _store = store;
        _projector = projector;
        _logger = logger;
    }

    public async Task<ClinicalReadResult> ReadAsync(
        ClinicalAccessContext context, string resourceType, string id, CancellationToken ct = default)
    {
        var type = ClinicalResourceInventory.Canonicalize(resourceType);
        if (type is null)
            return ClinicalReadResult.NotFound;

        if (string.IsNullOrWhiteSpace(context.TenantId) || string.IsNullOrWhiteSpace(context.AuthorizedMemberId))
        {
            Audit(context, type, id, "read", ClinicalAccessOutcome.NotAuthorized, 0);
            return ClinicalReadResult.NotAuthorized;
        }

        // An id CHO could not have issued names nothing; answer without a lookup,
        // and with the same 404 every other miss gets.
        if (!ClinicalResourceIdentity.IsWellFormed(id))
        {
            Audit(context, type, id, "read", ClinicalAccessOutcome.NotFound, 0);
            return ClinicalReadResult.NotFound;
        }

        // The member is IN the query. There is no moment at which another
        // member's row is in memory to be filtered out.
        var stored = await _store.GetAsync(context.TenantId, context.AuthorizedMemberId, type, id, ct);
        if (stored is null)
        {
            Audit(context, type, id, "read", ClinicalAccessOutcome.NotFound, 0);
            return ClinicalReadResult.NotFound;
        }

        var projected = await ProjectAsync(context.TenantId, context.AuthorizedMemberId, [stored], ct);
        if (projected.Count == 0)
        {
            // Stored but unreadable. Externally identical to "not there"; the
            // category is what the audit line records.
            Audit(context, type, id, "read", ClinicalAccessOutcome.NotFound, 0);
            return ClinicalReadResult.NotFound;
        }

        Audit(context, type, id, "read", ClinicalAccessOutcome.Granted, 1);
        return new ClinicalReadResult(ClinicalAccessOutcome.Granted, projected[0]);
    }

    public async Task<ClinicalSearchResult> SearchAsync(
        ClinicalAccessContext context,
        string resourceType,
        string? requestedMemberId,
        string? idFilter,
        int page,
        int count,
        CancellationToken ct = default)
    {
        var type = ClinicalResourceInventory.Canonicalize(resourceType);
        if (type is null)
            return ClinicalSearchResult.InvalidRequest;

        if (string.IsNullOrWhiteSpace(context.TenantId) || string.IsNullOrWhiteSpace(context.AuthorizedMemberId))
        {
            Audit(context, type, null, "search", ClinicalAccessOutcome.NotAuthorized, 0);
            return ClinicalSearchResult.NotAuthorized;
        }

        // A search that names a member other than the authorized one is refused
        // outright rather than quietly rewritten to the caller's own member: a
        // caller must not be able to probe for another member and get a
        // plausible-looking empty answer back.
        if (!string.IsNullOrWhiteSpace(requestedMemberId)
            && !string.Equals(requestedMemberId, context.AuthorizedMemberId, StringComparison.Ordinal))
        {
            Audit(context, type, null, "search", ClinicalAccessOutcome.NotAuthorized, 0);
            return ClinicalSearchResult.NotAuthorized;
        }

        // An _id that cannot be one of CHO's matches nothing — but it is a valid
        // search, so it returns an empty Bundle rather than an error.
        if (!string.IsNullOrWhiteSpace(idFilter) && !ClinicalResourceIdentity.IsWellFormed(idFilter))
        {
            Audit(context, type, idFilter, "search", ClinicalAccessOutcome.Granted, 0);
            return new ClinicalSearchResult(ClinicalAccessOutcome.Granted, [], 0);
        }

        var pageResult = await _store.SearchAsync(new ClinicalResourceQuery
        {
            TenantId = context.TenantId,
            MemberId = context.AuthorizedMemberId,
            ResourceType = type,
            ClinicalId = string.IsNullOrWhiteSpace(idFilter) ? null : idFilter,
            Page = page,
            Count = count,
        }, ct);

        var projected = await ProjectAsync(context.TenantId, context.AuthorizedMemberId, pageResult.Items, ct);

        // A stored row that cannot be read back as the type it is indexed under is
        // omitted rather than served half-formed. That is a data defect, not a
        // normal empty result, so it is surfaced by count — never by content.
        if (projected.Count != pageResult.Items.Count)
        {
            _logger.LogWarning(
                "Clinical search omitted {Omitted} of {Selected} stored {Resource} rows that could not be "
                + "projected: tenant={Tenant}",
                pageResult.Items.Count - projected.Count, pageResult.Items.Count,
                Clean(type), Clean(context.TenantId));
        }

        Audit(context, type, null, "search", ClinicalAccessOutcome.Granted, projected.Count);
        return new ClinicalSearchResult(ClinicalAccessOutcome.Granted, projected, pageResult.Total);
    }

    /// <summary>
    /// Projects a page of stored rows, resolving every local reference across the
    /// whole page in ONE store round trip rather than one per reference.
    /// Reference resolution is scoped to the same tenant and member, so it can
    /// never confirm the existence of anyone else's resource.
    /// </summary>
    private async Task<IReadOnlyList<Resource>> ProjectAsync(
        string tenantId, string memberId, IReadOnlyList<StoredClinicalResource> stored, CancellationToken ct)
    {
        if (stored.Count == 0) return [];

        var localIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stored)
            foreach (var id in ClinicalResourceProjector.LocalReferenceIds(row.ResourceJson))
                localIds.Add(id);

        var referenceTypes = localIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await _store.GetResourceTypesAsync(tenantId, memberId, localIds, ct);

        var projected = new List<Resource>(stored.Count);
        foreach (var row in stored)
        {
            var resource = _projector.Project(row, referenceTypes);
            if (resource is not null) projected.Add(resource);
        }

        return projected;
    }

    /// <summary>
    /// One PHI-free line per clinical access. Opaque identifiers, a category and
    /// a count — never a value, a code, a narrative, a payload or a token. CR/LF
    /// is stripped from every caller-influenced field so an identifier cannot
    /// forge a log entry (CWE-117).
    /// </summary>
    private void Audit(
        ClinicalAccessContext context,
        string resourceType,
        string? resourceId,
        string interaction,
        ClinicalAccessOutcome outcome,
        int resultCount)
    {
        var level = outcome == ClinicalAccessOutcome.Granted ? LogLevel.Information : LogLevel.Warning;

        _logger.Log(level,
            "Clinical access {Interaction}: tenant={Tenant} caller={Caller} context={Context} member={Member} "
            + "resource={Resource} id={ResourceId} outcome={Outcome} results={Results} at={At}",
            Clean(interaction),
            Clean(context.TenantId),
            Clean(context.CallerId),
            context.IsPatientContext ? "patient" : "provider",
            Clean(context.AuthorizedMemberId),
            Clean(resourceType),
            Clean(resourceId),
            outcome,
            resultCount,
            DateTime.UtcNow);
    }

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
