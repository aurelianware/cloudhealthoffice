using ClaimsService.Models;
using EngineModels = CloudHealthOffice.NcciEngine.Models;

namespace ClaimsService.Services.Adjudication.Mapping;

/// <summary>
/// Bridges the platform's <see cref="AdapterClaim"/> shape onto
/// <c>CloudHealthOffice.NcciEngine</c>'s <see cref="EngineModels.NcciScrubRequest"/>
/// (capability 5.7, mirrors 5.4's <see cref="ClaimToX12837Mapper"/>).
///
/// <para>
/// The engine ships as a domain-agnostic class library so it can run
/// against state-Medicaid EDI pipelines that feed it 837 transactions
/// directly (Phase 2 customer onboarding). Keeping the mapper in
/// claims-service preserves that future use case.
/// </para>
///
/// <para>
/// <b>Mapping fidelity (Decision 16).</b> Only the fields the engine's
/// rules actually inspect are populated:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="EngineModels.ClaimServiceLine.LineNumber"/> — required.</description></item>
///   <item><description><see cref="EngineModels.ClaimServiceLine.ProcedureCode"/> — required (5-char CPT/HCPCS).</description></item>
///   <item><description><see cref="EngineModels.ClaimServiceLine.Modifiers"/> — feeds the -59/X{EPSU} override pre-filter.</description></item>
///   <item><description><see cref="EngineModels.ClaimServiceLine.Units"/> — feeds MUE checks.</description></item>
///   <item><description><see cref="EngineModels.ClaimServiceLine.ServiceDate"/> — feeds quarter resolution + same-DOS pair grouping.</description></item>
///   <item><description><see cref="EngineModels.ClaimServiceLine.PlaceOfServiceCode"/> — preserved for completeness; engine uses claim-level <see cref="EngineModels.NcciScrubRequest.ClaimType"/> for professional/facility MUE selection today.</description></item>
/// </list>
/// </summary>
public static class ClaimToNcciScrubRequestMapper
{
    /// <summary>
    /// Build the engine input from the claim. Lines whose procedure
    /// code or units fail the engine's data-annotation validation
    /// (5-char CPT/HCPCS, units in [0.01, 9999]) are filtered out — the
    /// stage falls back to a soft-pass when no valid lines remain.
    /// </summary>
    public static EngineModels.NcciScrubRequest Map(AdapterClaim claim)
    {
        var serviceLines = claim.ClaimLines
            .Where(IsLineEngineValid)
            .Select(MapServiceLine)
            .ToList();

        return new EngineModels.NcciScrubRequest
        {
            TenantId = claim.TenantId ?? string.Empty,
            ClaimId = claim.Id ?? string.Empty,
            ClaimType = MapClaimType(claim.ClaimType),
            ServiceLines = serviceLines,
            EffectiveDate = ResolveEffectiveDate(serviceLines, claim.ServiceDateFrom),
        };
    }

    /// <summary>
    /// True when the claim line carries enough field shape to satisfy
    /// the engine's data-annotation validation AND a non-default
    /// service date (the engine resolves quarter / pair-grouping by
    /// the line's <c>ServiceDate</c> and a missing date would be
    /// non-deterministic). Lines that fail are silently dropped; if no
    /// valid lines remain the stage falls back to a soft-pass with a
    /// structured warning rather than letting the engine throw at the
    /// boundary.
    /// </summary>
    public static bool IsLineEngineValid(AdapterClaimLine line) =>
        !string.IsNullOrWhiteSpace(line.ProcedureCode)
        && line.ProcedureCode!.Length == 5
        && line.Units >= 0.01m
        && line.Units <= 9999m
        && line.ServiceDateFrom != default;

    // 837P/837I/837D are the engine's expected wire-format strings. The
    // platform enum is 1-based (Professional=1, Institutional=2,
    // Dental=3); switch by name, never raw-cast.
    internal static string MapClaimType(ClaimType type) => type switch
    {
        ClaimType.Professional => "837P",
        ClaimType.Institutional => "837I",
        ClaimType.Dental => "837D",
        _ => "837P",
    };

    private static EngineModels.ClaimServiceLine MapServiceLine(AdapterClaimLine line) => new()
    {
        LineNumber = line.LineNumber,
        ProcedureCode = line.ProcedureCode,
        Modifiers = line.Modifiers?.Where(m => !string.IsNullOrEmpty(m)).ToList() ?? new List<string>(),
        Units = line.Units,
        // Caller has already filtered out lines with default ServiceDateFrom
        // via IsLineEngineValid, so the conversion is unconditional —
        // no DateTime.UtcNow fallback that would non-deterministically
        // resolve to the current quarter for malformed claim data.
        ServiceDate = DateOnly.FromDateTime(line.ServiceDateFrom),
        PlaceOfServiceCode = line.PlaceOfServiceCode,
    };

    /// <summary>
    /// Earliest line-level service date wins (Decision 7) so the
    /// applicable NCCI / MUE quarter is resolved against the most
    /// restrictive date the claim asserts. Falls back to claim header
    /// <see cref="AdapterClaim.ServiceDateFrom"/> when no lines remain
    /// after engine-validation filtering.
    /// </summary>
    internal static DateOnly? ResolveEffectiveDate(
        IReadOnlyList<EngineModels.ClaimServiceLine> serviceLines,
        DateTime headerServiceDateFrom)
    {
        if (serviceLines.Count > 0)
        {
            return serviceLines.Min(l => l.ServiceDate);
        }
        return headerServiceDateFrom == default
            ? null
            : DateOnly.FromDateTime(headerServiceDateFrom);
    }
}
