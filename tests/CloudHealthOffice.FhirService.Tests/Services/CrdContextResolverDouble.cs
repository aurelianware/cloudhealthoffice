using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using FhirService.Services;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// A stand-in for <see cref="ICrdContextResolver"/>, so a test about how a
/// determination is reached does not also have to stand up a member and a
/// coverage record.
///
/// <para>
/// <see cref="Resolved"/> is the default for tests written before CRD resolved a
/// real context: they assert rule-engine behaviour, and the engine is only
/// called when a context resolved, so handing them a resolved one keeps them
/// testing what they were written to test.
/// </para>
/// </summary>
internal sealed class CrdContextResolverDouble : ICrdContextResolver
{
    private readonly CrdContextResolution _resolution;

    private CrdContextResolverDouble(CrdContextResolution resolution) => _resolution = resolution;

    /// <summary>How many times the resolver was asked. One hook should ask once.</summary>
    public int Calls { get; private set; }

    public static CrdContextResolverDouble Resolved(
        string stateCode = "TX",
        PaLineOfBusiness lob = PaLineOfBusiness.Medicaid,
        string? program = null,
        DateOnly? memberDateOfBirth = null,
        IReadOnlyList<string>? diagnosisCodes = null) =>
        new(CrdContextResolution.Resolved(new PaRuleContext
        {
            TenantId = "tenant",
            StateCode = stateCode,
            Lob = lob,
            Program = program,
            RequestingProviderNpi = "1234567893",
            ServicingProviderNpi = "1234567893",
            MemberId = "pat-001",
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            ProcedureCodes = [],
            DiagnosisCodes = diagnosisCodes ?? [],
            EstimatedCost = 0m,
            MemberDateOfBirth = memberDateOfBirth,
        }));

    public static CrdContextResolverDouble Unresolved(CrdContextFailure failure) =>
        new(CrdContextResolution.Unresolved(failure));

    public Task<CrdContextResolution> ResolveAsync(
        CrdHookRequest request, string tenantId, IReadOnlyList<string> procedureCodes,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(_resolution);
    }
}
