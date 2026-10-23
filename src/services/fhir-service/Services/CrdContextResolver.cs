using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using FhirService.Services.ChoNative;
using Hl7.Fhir.Model;

namespace FhirService.Services;

/// <summary>Why CRD could not build a member context, when it could not.</summary>
public enum CrdContextFailure
{
    /// <summary>No Patient for the hook's <c>context.patientId</c>. IG reason <c>no-member-found</c>.</summary>
    MemberNotFound,

    /// <summary>The member exists but has no active Coverage. IG reason <c>no-active-coverage</c>.</summary>
    NoActiveCoverage,

    /// <summary>The member and coverage exist but do not carry what the rules need. IG reason <c>coverage-not-found</c>.</summary>
    ContextIncomplete,

    /// <summary>A store CHO needed was unreachable or errored. IG reason <c>technical</c>.</summary>
    Unavailable,
}

/// <summary>
/// The outcome of resolving a CRD member context: either a context the rule
/// engine can be trusted with, or the reason there is none.
/// </summary>
public sealed record CrdContextResolution
{
    public PaRuleContext? Context { get; init; }
    public CrdContextFailure? Failure { get; init; }

    public bool IsResolved => Context is not null;

    public static CrdContextResolution Resolved(PaRuleContext context) => new() { Context = context };
    public static CrdContextResolution Unresolved(CrdContextFailure failure) => new() { Failure = failure };
}

public interface ICrdContextResolver
{
    Task<CrdContextResolution> ResolveAsync(
        CrdHookRequest request, string tenantId, IReadOnlyList<string> procedureCodes,
        CancellationToken ct = default);
}

/// <summary>
/// Builds the prior-authorization rule context for a CRD hook from the member CHO
/// actually holds.
///
/// <para>
/// D-21, second part, and G-05. CRD called the rule engine with a hard-coded
/// <c>TX</c> / <c>Medicaid</c> / <c>STAR</c> context, no diagnoses, no member
/// date of birth and a cost of zero — for every member, in every state, on
/// every line of business. That is not an incomplete context, it is a wrong
/// one: it resolves a Texas Medicaid STAR rule set for a commercial member in
/// Ohio, and whatever that rule set says is then reported as CHO's coverage
/// determination.
/// </para>
///
/// <para>
/// So the context is read from CHO's own Patient and Coverage, and when it
/// cannot be read the engine is <b>not called at all</b>. That is the only
/// fail-closed answer available: a fabricated context does not produce a weaker
/// determination, it produces a confident one about somebody else's benefits.
/// An administrator's explicit code classification still applies in that case,
/// because listing a code as requiring authorization is not a statement about
/// any particular member.
/// </para>
///
/// <para>
/// CHO's own records are preferred over the EHR's prefetch deliberately. CHO is
/// the payer: its Coverage is the authority on line of business, and the
/// prefetch Coverage is a copy of it. The one thing only the EHR has is why the
/// service was ordered, so the diagnoses come from the draft order.
/// </para>
///
/// <para>
/// <b>Where the three keys come from.</b> The rule engine keys its rule sets on
/// state, line of business and program, and a <b>program</b> record carries all
/// three (docs/architecture/plan-configuration.md §3). So when the member's
/// coverage names a benefit plan and that plan is sold under a program, all
/// three are taken from the program: it is the line of business the contract
/// was actually written against, and the state the plan operates in, rather
/// than whichever state the member's mailing address happens to be in. Without
/// a program CHO still has no program code, and state and line of business come
/// from the member's address and their coverage's own coding as before — a
/// correct answer, just a less specific one.
/// </para>
/// </summary>
public sealed class CrdContextResolver : ICrdContextResolver
{
    private readonly IFhirDataAdapter _adapter;
    private readonly ILogger<CrdContextResolver> _logger;
    private readonly IProgramDirectory? _programs;

    public CrdContextResolver(
        IFhirDataAdapter adapter, ILogger<CrdContextResolver> logger, IProgramDirectory? programs = null)
    {
        _adapter = adapter;
        _logger = logger;
        _programs = programs;
    }

    public async Task<CrdContextResolution> ResolveAsync(
        CrdHookRequest request, string tenantId, IReadOnlyList<string> procedureCodes,
        CancellationToken ct = default)
    {
        var patientId = StripPrefix(request.Context?.PatientId);
        if (string.IsNullOrWhiteSpace(patientId))
        {
            return CrdContextResolution.Unresolved(CrdContextFailure.MemberNotFound);
        }

        Patient? patient;
        Coverage? coverage;
        try
        {
            patient = await _adapter.GetPatientAsync(patientId, tenantId, ct);
            if (patient is null)
            {
                return CrdContextResolution.Unresolved(CrdContextFailure.MemberNotFound);
            }

            coverage = await ActiveCoverageAsync(patientId, tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable is not the same as absent, and the IG has a different
            // reason code for each. Reporting "member not found" when a store was
            // simply down would tell a provider to stop looking.
            _logger.LogWarning(ex,
                "CRD could not read the member context for {PatientId}; the rule engine will not be called",
                LogSanitizer.MaskId(patientId));
            return CrdContextResolution.Unresolved(CrdContextFailure.Unavailable);
        }

        if (coverage is null)
        {
            return CrdContextResolution.Unresolved(CrdContextFailure.NoActiveCoverage);
        }

        ProgramDescriptor? program;
        try
        {
            program = await ProgramAsync(coverage, tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The coverage names a benefit plan, so there may well be a program
            // with a state and a line of business of its own, and falling back
            // to the member's address could resolve an Ohio rule set for a
            // Texas STAR member. "Could not look" is reported as technical,
            // which is the reason code the IG has for exactly this.
            _logger.LogWarning(ex,
                "CRD could not read the program for {PatientId}'s benefit plan; the rule engine will not be called",
                LogSanitizer.MaskId(patientId));
            return CrdContextResolution.Unresolved(CrdContextFailure.Unavailable);
        }

        // A program states all three keys together. Without one, the member's
        // address and their coverage's own line-of-business coding, as before.
        var state = program?.StateCode ?? State(patient);
        var lob = program is null ? LineOfBusiness(coverage) : LineOfBusiness(program);

        // The rule set is keyed on state and line of business. Without both,
        // there is no rule set to resolve and nothing to be gained by guessing
        // one.
        if (state is null || lob is null)
        {
            return CrdContextResolution.Unresolved(CrdContextFailure.ContextIncomplete);
        }

        var npi = PractitionerNpi(request);

        return CrdContextResolution.Resolved(new PaRuleContext
        {
            TenantId = tenantId,
            StateCode = state,
            Lob = lob.Value,
            // The program's own code ("STAR"), or null where the plan carries no
            // program — which is still the truth, not a guess, and is what
            // PaRuleContext documents for a non-program state. Hard-coding STAR
            // made every member a Texas STAR member.
            Program = program?.Code,
            // The record behind that code, so a decision's audit trail names a
            // program that can be looked up rather than a string built out of
            // a state, a line of business and a code (plan-configuration.md
            // §12.6).
            ProgramId = program?.Id,
            RequestingProviderNpi = npi,
            ServicingProviderNpi = npi,
            MemberId = patientId,
            ServiceDate = ServiceDate(request),
            ProcedureCodes = procedureCodes,
            DiagnosisCodes = DiagnosisCodes(request),
            // Not derivable at order time: nothing in a draft order states what
            // CHO will be charged. Left at zero, as before, rather than invented.
            EstimatedCost = 0m,
            MemberDateOfBirth = BirthDate(patient),
        });
    }

    private async Task<Coverage?> ActiveCoverageAsync(string patientId, string tenantId, CancellationToken ct)
    {
        var (items, _) = await _adapter.SearchCoverageAsync(
            new CoverageSearchParams { Patient = patientId, Status = "active" }, tenantId, ct);

        // The search filter is honoured by the ChoNative adapter, but an adapter
        // that ignored it must not be allowed to produce a determination against
        // lapsed coverage.
        return items.FirstOrDefault(c => c.Status is null or FinancialResourceStatusCodes.Active);
    }

    /// <summary>
    /// The program the member's benefit plan is sold under, or null when the
    /// coverage names no plan, the plan carries no program, or this host has no
    /// program directory. Throws when the directory could not be asked, so an
    /// outage is never read as "no program".
    /// </summary>
    private async Task<ProgramDescriptor?> ProgramAsync(Coverage coverage, string tenantId, CancellationToken ct)
    {
        if (_programs is null) return null;
        var planId = BenefitPlanId(coverage);
        return planId is null ? null : await _programs.ForBenefitPlanAsync(tenantId, planId, ct);
    }

    /// <summary>
    /// The benefit plan the coverage names, from its <c>plan</c> class — the
    /// class coverage-service projects <c>Coverage.PlanId</c> into.
    /// </summary>
    internal static string? BenefitPlanId(Coverage coverage) =>
        coverage.Class
            ?.Where(c => c.Type?.Coding?.Any(coding =>
                coding.System == CoverageClassSystem && string.Equals(coding.Code, "plan", StringComparison.Ordinal)) == true)
            .Select(c => c.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();

    /// <summary>The HL7 coverage-class system coverage-service projects plan and group classes under.</summary>
    internal const string CoverageClassSystem = "http://terminology.hl7.org/CodeSystem/coverage-class";

    /// <summary>
    /// The line of business a program states. A program whose line of business
    /// the rule engine has no member for resolves nothing, so it answers null
    /// rather than a neighbouring value.
    /// </summary>
    internal static PaLineOfBusiness? LineOfBusiness(ProgramDescriptor program) =>
        Enum.TryParse<PaLineOfBusiness>(program.LineOfBusiness, ignoreCase: true, out var lob) ? lob : null;

    /// <summary>The member's state, from the first address that states one.</summary>
    internal static string? State(Patient patient) =>
        patient.Address
            ?.Select(address => address.State)
            .FirstOrDefault(state => !string.IsNullOrWhiteSpace(state));

    /// <summary>
    /// The line of business, from the CHO line-of-business coding on
    /// <c>Coverage.type</c>.
    ///
    /// <para>
    /// Read only from CHO's own code system. A coding from somewhere else may
    /// use the same words for a different concept, and a line of business read
    /// wrongly selects the wrong rule set — the failure this whole resolver
    /// exists to stop.
    /// </para>
    /// </summary>
    internal static PaLineOfBusiness? LineOfBusiness(Coverage coverage)
    {
        var codes = coverage.Type?.Coding
            ?.Where(coding => coding.System == ChoNativeFhirDataAdapter.LineOfBusinessSystem)
            .Select(coding => coding.Code)
            ?? [];

        foreach (var code in codes)
        {
            if (Enum.TryParse<PaLineOfBusiness>(code, ignoreCase: true, out var lob))
            {
                return lob;
            }
        }

        return null;
    }

    internal static DateOnly? BirthDate(Patient patient) =>
        DateOnly.TryParse(patient.BirthDate, out var birthDate) ? birthDate : null;

    /// <summary>
    /// The diagnoses the EHR gave as the reason for the order. The one piece of
    /// context CHO cannot supply itself, and the reason a DiagnosisRequired rule
    /// used to pass vacuously: it was always handed an empty list.
    /// </summary>
    internal static List<string> DiagnosisCodes(CrdHookRequest request) =>
        (request.Context?.DraftOrders?.Entry ?? [])
            .Select(entry => entry.Resource)
            .Where(resource => resource is not null)
            .SelectMany(resource => resource!.ReasonCode ?? [])
            .SelectMany(concept => concept.Coding ?? [])
            .Select(coding => coding.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// When the service is to be performed, from the first draft order that says
    /// so. Falls back to today, which is what a same-day order means and what
    /// CRD assumed unconditionally before.
    /// </summary>
    internal static DateOnly ServiceDate(CrdHookRequest request)
    {
        foreach (var entry in request.Context?.DraftOrders?.Entry ?? [])
        {
            var stated = entry.Resource?.OccurrenceDateTime
                         ?? entry.Resource?.OccurrencePeriod?.Start;

            if (DateTimeOffset.TryParse(stated, out var occurrence))
            {
                return DateOnly.FromDateTime(occurrence.UtcDateTime);
            }
        }

        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    /// <summary>The practitioner identifier from the hook's <c>context.userId</c>.</summary>
    internal static string PractitionerNpi(CrdHookRequest request) =>
        StripPrefix(request.Context?.UserId) ?? string.Empty;

    private static string? StripPrefix(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var slash = reference.LastIndexOf('/');
        return slash >= 0 && slash < reference.Length - 1 ? reference[(slash + 1)..] : reference;
    }
}
