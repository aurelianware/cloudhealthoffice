using TenantService.Hierarchy;

namespace TenantService.Configuration;

/// <summary>A program as narrowing an override list needs it: its node id and the line of business it runs under.</summary>
public sealed record ProgramNode(string Id, string LineOfBusiness);

/// <summary>
/// The nodes under a node, so that a list of overrides can be narrowed to one
/// branch of the hierarchy (plan-configuration.md §12.6).
///
/// <para>
/// Every method throws rather than answering an empty list when the records
/// could not be read, because a narrowing built on "no descendants" would
/// silently drop every override below the node — the same "could not look"
/// against "there is none" distinction the directories keep.
/// </para>
/// </summary>
public interface IHierarchyDescendants
{
    /// <summary>The tenant's programs under one carrier, or all of them when no carrier is named.</summary>
    Task<IReadOnlyList<ProgramNode>> ProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default);

    /// <summary>The plan ids of every benefit plan in the named programs.</summary>
    Task<IReadOnlyList<string>> BenefitPlanIdsAsync(string tenantId, IReadOnlyCollection<string> programIds, CancellationToken ct = default);
}

/// <summary>
/// Reads programs from tenant-service's own hierarchy records and plans from
/// benefit-plan-service, which is where each link lives.
/// </summary>
public sealed class HierarchyDescendants(HierarchyService hierarchy, IBenefitPlanProgramLookup plans) : IHierarchyDescendants
{
    public async Task<IReadOnlyList<ProgramNode>> ProgramsAsync(string tenantId, string? carrierId, CancellationToken ct = default) =>
        (await hierarchy.ListProgramsAsync(tenantId, carrierId, ct))
            .Select(p => new ProgramNode(p.Id, p.LineOfBusiness))
            .ToList();

    public async Task<IReadOnlyList<string>> BenefitPlanIdsAsync(string tenantId, IReadOnlyCollection<string> programIds,
        CancellationToken ct = default)
    {
        var ids = new List<string>();
        foreach (var programId in programIds)
            ids.AddRange(await plans.GetPlanIdsForProgramAsync(tenantId, programId, ct));
        return ids;
    }
}
