using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace SponsorService.Contracts;

/// <summary>What a caller may set on one contract year of a policy's terms.</summary>
public sealed record PolicyTermsWrite
{
    public RateTable? Rates { get; init; }
    public ContributionTerms? Contribution { get; init; }
    public EligibilityTerms? Eligibility { get; init; }
    public List<ClassTerms>? ClassTerms { get; init; }
    public EnrollmentTerms? Enrollment { get; init; }
    public TerminationTerms? Termination { get; init; }
    public List<LifeEventTerms>? LifeEvents { get; init; }
}

/// <summary>Why a date could not be resolved to a version of a policy's terms.</summary>
public enum TermsResolutionOutcome
{
    /// <summary>A version was found, and it is in <c>Terms</c>.</summary>
    Resolved,

    /// <summary>
    /// The group has no contract term signed or active covering the date.
    /// Every group is in this state until a contract is entered, so a consumer
    /// falls back on what it reads today rather than treating it as a failure.
    /// </summary>
    NoContractTerm,

    /// <summary>The policy is not offered on that date: it had ended, or had not started.</summary>
    PolicyNotOffered,

    /// <summary>
    /// There <b>is</b> a contract covering the date and this policy's terms
    /// for it were never entered. Distinct from <see cref="NoContractTerm"/>
    /// on purpose, and the one outcome a consumer must not paper over: last
    /// year's rates are not this year's, and a contract with a hole in it is
    /// not the same as no contract. Whoever is resolving should stop and say
    /// the terms are missing.
    /// </summary>
    TermsNotEntered,
}

/// <summary>
/// What resolving a date against a policy came to: the version in force, the
/// contract term that covers it, and — where there is none — which of the
/// several quite different reasons applies.
/// </summary>
public sealed record TermsResolution(
    TermsResolutionOutcome Outcome,
    PolicyTerms? Terms = null,
    ContractTerm? ContractTerm = null,
    SponsorPolicy? Policy = null);

/// <summary>
/// A policy's terms, versioned per contract period
/// (docs/architecture/plan-configuration.md §9b; step 3b of the §12 build
/// order). Nothing reads these yet: the consumers move one at a time in 3d.
///
/// <para>
/// Resolving a date goes <b>through the contract term</b> rather than past it.
/// §9b says the term covers that year's policy terms, so a version is in force
/// only while a term that is signed or active covers the date — a draft year's
/// rates are a quote, not something to bill from. That is also why a version is
/// keyed on the contract period instead of carrying dates of its own: two
/// records with their own dates would eventually disagree about which year a
/// date belongs to.
/// </para>
/// </summary>
public sealed class PolicyTermsService(
    IContractStore store,
    ContractService contracts,
    TimeProvider clock,
    ILogger<PolicyTermsService> logger)
{
    private static readonly Regex EventCodePattern = new(@"^[A-Z0-9][A-Z0-9_\-]{0,31}$", RegexOptions.Compiled);

    /// <summary>Every version of a policy's terms, oldest period first.</summary>
    public async Task<ContractResult<IReadOnlyList<PolicyTerms>>> ListAsync(
        string tenantId, string groupNumber, string policyNumber, CancellationToken ct = default)
    {
        var found = await contracts.GetPolicyAsync(tenantId, groupNumber, policyNumber, ct);
        if (found.Outcome != ContractOutcome.Ok)
            return new ContractResult<IReadOnlyList<PolicyTerms>>(found.Outcome, found.Errors);
        return ContractResult<IReadOnlyList<PolicyTerms>>.Ok(
            await store.ListPolicyTermsAsync(tenantId, found.Value!.Id, ct));
    }

    /// <summary>One contract year of a policy's terms, by the period's start date.</summary>
    public async Task<ContractResult<PolicyTerms>> GetAsync(
        string tenantId, string groupNumber, string policyNumber, DateTime periodStart, CancellationToken ct = default)
    {
        var found = await contracts.GetPolicyAsync(tenantId, groupNumber, policyNumber, ct);
        if (found.Outcome != ContractOutcome.Ok)
            return new ContractResult<PolicyTerms>(found.Outcome, found.Errors);

        var id = PolicyTerms.IdFor(found.Value!.Id, periodStart.Date);
        return await store.GetPolicyTermsAsync(tenantId, id, ct) is { } terms
            ? ContractResult<PolicyTerms>.Ok(terms)
            : ContractResult<PolicyTerms>.Fail(ContractOutcome.NotFound,
                $"Policy '{policyNumber}' has no terms entered for the contract period starting {periodStart:yyyy-MM-dd}.");
    }

    /// <summary>
    /// Enters or restates one contract year of a policy's terms. The contract
    /// term for the period has to exist first, which is §9b's order of events:
    /// a renewal opens a draft term, and the account manager then enters the
    /// plans and their terms into it.
    /// </summary>
    public async Task<ContractResult<PolicyTerms>> UpsertAsync(
        ICurrentActor actor, string groupNumber, string policyNumber, DateTime periodStart,
        PolicyTermsWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        var found = await contracts.GetPolicyAsync(tenantId, groupNumber, policyNumber, ct);
        if (found.Outcome != ContractOutcome.Ok) return new ContractResult<PolicyTerms>(found.Outcome, found.Errors);
        var policy = found.Value!;

        var term = await store.GetTermAsync(tenantId, ContractTerm.IdFor(tenantId, groupNumber, periodStart.Date), ct);
        if (term is null)
            return ContractResult<PolicyTerms>.Fail(ContractOutcome.NotFound,
                $"Group '{groupNumber}' has no contract term starting {periodStart:yyyy-MM-dd}. Open the term first, then enter the terms of each policy under it.");

        // A signed contract's terms are what both sides agreed to, so they are
        // not editable in place. An amendment is a contract event: end this
        // period and open the next, or open a new period for the amendment,
        // either of which leaves a record of what changed and when.
        if (term.Status is ContractTermStatus.Signed or ContractTermStatus.Active)
            return ContractResult<PolicyTerms>.Fail(ContractOutcome.Conflict,
                $"The contract term starting {periodStart:yyyy-MM-dd} is {term.Status.ToString().ToLowerInvariant()}, so its terms are settled. Amend it by opening a new contract period, so that what changed and when stays on the record.");
        if (term.Status is ContractTermStatus.Ended)
            return ContractResult<PolicyTerms>.Fail(ContractOutcome.Conflict,
                $"The contract term starting {periodStart:yyyy-MM-dd} has ended; its terms cannot be changed.");

        var errors = Validate(write, policy).ToList();
        if (errors.Count > 0) return new ContractResult<PolicyTerms>(ContractOutcome.Invalid, errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var id = PolicyTerms.IdFor(policy.Id, periodStart.Date);
        var existing = await store.GetPolicyTermsAsync(tenantId, id, ct);

        var terms = existing ?? new PolicyTerms
        {
            Id = id,
            TenantId = tenantId,
            GroupNumber = policy.GroupNumber,
            PolicyId = policy.Id,
            PeriodStart = periodStart.Date,
            CreatedAt = now,
            CreatedBy = actor.UserId,
        };

        terms.Rates = write.Rates is null ? terms.Rates : Canonical(write.Rates);
        terms.Contribution = write.Contribution ?? terms.Contribution;
        terms.Eligibility = write.Eligibility is null ? terms.Eligibility : Canonical(write.Eligibility);
        if (write.ClassTerms is not null) terms.ClassTerms = write.ClassTerms.Select(Canonical).ToList();
        terms.Enrollment = write.Enrollment ?? terms.Enrollment;
        terms.Termination = write.Termination ?? terms.Termination;
        if (write.LifeEvents is not null) terms.LifeEvents = write.LifeEvents.Select(Canonical).ToList();
        terms.UpdatedAt = now;
        terms.UpdatedBy = actor.UserId;

        if (existing is null)
        {
            await store.InsertPolicyTermsAsync(terms, ct);
            logger.LogInformation(
                "Entered terms for policy {Policy} in contract period {Period}",
                LogSanitizer.Sanitize(policy.PolicyNumber), periodStart.ToString("yyyy-MM-dd"));
            return ContractResult<PolicyTerms>.Ok(terms);
        }

        if (!await store.ReplacePolicyTermsAsync(terms, existing.Revision, ct))
            return ContractResult<PolicyTerms>.Fail(ContractOutcome.Conflict,
                "These terms changed while this edit was being made; read them again.");
        return ContractResult<PolicyTerms>.Ok(terms);
    }

    /// <summary>
    /// The version of a policy's terms to use for <paramref name="on"/>, found
    /// the way §9b says: the contract term signed or active over that date, and
    /// then that period's terms. The outcome says which of the reasons applies
    /// where there is none, because they are not interchangeable — see
    /// <see cref="TermsResolutionOutcome"/>.
    /// </summary>
    public async Task<TermsResolution> ResolveAsync(
        string tenantId, string groupNumber, string policyNumber, DateTime on, CancellationToken ct = default)
    {
        var found = await contracts.GetPolicyAsync(tenantId, groupNumber, policyNumber, ct);
        if (found.Outcome != ContractOutcome.Ok) return new TermsResolution(TermsResolutionOutcome.PolicyNotOffered);
        var policy = found.Value!;

        var term = await contracts.TermInForceAsync(tenantId, groupNumber, on, ct);
        if (term is null) return new TermsResolution(TermsResolutionOutcome.NoContractTerm, Policy: policy);

        if (!policy.IsOffered(on))
            return new TermsResolution(TermsResolutionOutcome.PolicyNotOffered, ContractTerm: term, Policy: policy);

        var terms = await store.GetPolicyTermsAsync(tenantId, PolicyTerms.IdFor(policy.Id, term.PeriodStart.Date), ct);
        if (terms is null)
        {
            // Loud, because this is a contract with a hole in it rather than a
            // group without a contract, and the difference decides whether a
            // consumer may fall back.
            logger.LogWarning(
                "Contract term {Period} is {Status} for group {Group} but policy {Policy} has no terms entered for it",
                term.PeriodStart.ToString("yyyy-MM-dd"), term.Status,
                LogSanitizer.Sanitize(groupNumber), LogSanitizer.Sanitize(policy.PolicyNumber));
            return new TermsResolution(TermsResolutionOutcome.TermsNotEntered, ContractTerm: term, Policy: policy);
        }

        return new TermsResolution(TermsResolutionOutcome.Resolved, terms, term, policy);
    }

    // ── Validation ────────────────────────────────────────────────

    private static IEnumerable<string> Validate(PolicyTermsWrite write, SponsorPolicy policy)
    {
        foreach (var e in RateErrors(write.Rates, "The policy's rates")) yield return e;
        foreach (var e in ContributionErrors(write.Contribution)) yield return e;
        foreach (var e in EligibilityErrors(write.Eligibility, "The policy's eligibility")) yield return e;
        foreach (var e in ClassTermErrors(write.ClassTerms, policy)) yield return e;
        foreach (var e in EnrollmentErrors(write.Enrollment)) yield return e;
        foreach (var e in TerminationErrors(write.Termination)) yield return e;
        foreach (var e in LifeEventErrors(write.LifeEvents)) yield return e;
    }

    private static IEnumerable<string> RateErrors(RateTable? table, string what)
    {
        if (table is null) yield break;

        var wanted = RateTable.TiersOf(table.Structure);
        var quoted = table.Tiers.Select(t => t.Tier).ToList();

        // A missing tier is a household shape nobody can bill, and a tier from
        // another structure is a rate nothing will ever look up, so the table
        // has to quote exactly the structure's tiers.
        foreach (var missing in wanted.Except(quoted))
            yield return $"{what}: a {Spell(table.Structure)} rate table needs a rate for {Spell(missing)}.";
        foreach (var extra in quoted.Distinct().Except(wanted))
            yield return $"{what}: {Spell(extra)} is not a tier of a {Spell(table.Structure)} structure.";
        foreach (var twice in quoted.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key))
            yield return $"{what}: {Spell(twice)} is quoted twice.";
        if (table.Tiers.Any(t => t.Amount < 0))
            yield return $"{what}: a rate cannot be negative.";
    }

    private static IEnumerable<string> ContributionErrors(ContributionTerms? contribution)
    {
        if (contribution is null) yield break;
        foreach (var (share, what) in new[]
                 {
                     (contribution.EmployerSharePercentEmployee, "employee"),
                     (contribution.EmployerSharePercentDependents, "dependent"),
                 })
        {
            if (share is { } percent && percent is < 0 or > 100)
                yield return $"The employer's {what} share must be a percentage between 0 and 100.";
        }
    }

    private static IEnumerable<string> EligibilityErrors(EligibilityTerms? eligibility, string what)
    {
        if (eligibility is null) yield break;
        if (eligibility.MinimumHoursPerWeek is { } hours && hours is < 0 or > 168)
            yield return $"{what}: the minimum hours a week must be between 0 and 168.";

        if (eligibility.Dependents?.ChildAgeLimit is { } limit && limit < ContractFloors.MinChildAgeLimit)
            yield return $"{what}: a child must stay eligible to at least {ContractFloors.MinChildAgeLimit}, so an age limit of {limit} cannot be entered. A contract may be more generous than the floor, not less.";
    }

    private static IEnumerable<string> ClassTermErrors(List<ClassTerms>? classTerms, SponsorPolicy policy)
    {
        if (classTerms is null) yield break;

        var onPolicy = policy.Classes.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in classTerms)
        {
            var code = c.ClassCode?.Trim().ToUpperInvariant() ?? string.Empty;
            if (code.Length == 0) { yield return "Every set of class terms names a class."; continue; }

            // Terms for a class the policy does not have are terms no member
            // can ever be placed under, and they would read as a real answer to
            // anything asking about that class.
            if (!onPolicy.Contains(code))
                yield return $"Policy '{policy.PolicyNumber}' has no class '{code}'. Add the class to the policy before entering its terms.";
            if (!seen.Add(code)) yield return $"Class '{code}' has two sets of terms.";

            foreach (var e in RateErrors(c.Rates, $"Class '{code}' rates")) yield return e;
            foreach (var e in EligibilityErrors(c.Eligibility, $"Class '{code}' eligibility")) yield return e;
        }
    }

    private static IEnumerable<string> EnrollmentErrors(EnrollmentTerms? enrollment)
    {
        if (enrollment is null) yield break;
        if (enrollment.WaitingPeriodDays is { } days)
        {
            if (days < 0) yield return "A waiting period cannot be negative.";
            else if (days > ContractFloors.MaxWaitingPeriodDays)
                yield return $"A group waiting period cannot be longer than {ContractFloors.MaxWaitingPeriodDays} days, so {days} cannot be entered.";
        }
        if (enrollment.RetroEnrollmentDays is { } retro && retro < 0)
            yield return "A retroactive enrolment window cannot be negative.";
    }

    private static IEnumerable<string> TerminationErrors(TerminationTerms? termination)
    {
        if (termination?.RetroTerminationDays is { } days && days < 0)
            yield return "A retroactive termination window cannot be negative.";
    }

    private static IEnumerable<string> LifeEventErrors(List<LifeEventTerms>? events)
    {
        if (events is null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            var code = e.Code?.Trim().ToUpperInvariant() ?? string.Empty;
            if (code.Length == 0) { yield return "Every life event needs a code."; continue; }
            if (!EventCodePattern.IsMatch(code))
                yield return $"Life event code '{e.Code}' is not up to 32 characters of A-Z, 0-9, '-' and '_'.";
            if (!seen.Add(code)) yield return $"Life event '{code}' is listed twice.";

            if (e.ReportingWindowDays is { } window)
            {
                if (window < 0) yield return $"Life event '{code}': a reporting window cannot be negative.";
                else if (ContractFloors.MinWindowFor(code) is { } floor && window < floor)
                    yield return $"Life event '{code}' has a federal special-enrolment window of at least {floor} days, so {window} cannot be entered.";
            }
        }
    }

    // ── Canonical forms ───────────────────────────────────────────

    private static RateTable Canonical(RateTable table) => new()
    {
        Structure = table.Structure,
        Tiers = RateTable.TiersOf(table.Structure)
            .Select(tier => new TierRate { Tier = tier, Amount = table.For(tier) ?? 0m })
            .ToList(),
    };

    private static EligibilityTerms Canonical(EligibilityTerms eligibility) => new()
    {
        MinimumHoursPerWeek = eligibility.MinimumHoursPerWeek,
        EmployeeTypes = eligibility.EmployeeTypes
            .Select(t => t?.Trim().ToUpperInvariant() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList(),
        Dependents = eligibility.Dependents,
    };

    private static ClassTerms Canonical(ClassTerms terms) => new()
    {
        ClassCode = terms.ClassCode?.Trim().ToUpperInvariant() ?? string.Empty,
        Rates = terms.Rates is null ? null : Canonical(terms.Rates),
        Eligibility = terms.Eligibility is null ? null : Canonical(terms.Eligibility),
    };

    private static LifeEventTerms Canonical(LifeEventTerms e) => new()
    {
        Code = e.Code?.Trim().ToUpperInvariant() ?? string.Empty,
        ReportingWindowDays = e.ReportingWindowDays,
        CoverageStartRule = e.CoverageStartRule,
        DocumentationRequired = e.DocumentationRequired
            .Select(d => d?.Trim().ToUpperInvariant() ?? string.Empty)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList(),
    };

    private static string Spell(TierStructure structure) => structure switch
    {
        TierStructure.TwoTier => "two-tier",
        TierStructure.ThreeTier => "three-tier",
        TierStructure.FourTier => "four-tier",
        _ => structure.ToString(),
    };

    private static string Spell(RateTier tier) => tier switch
    {
        RateTier.Employee => "employee",
        RateTier.EmployeePlusOne => "employee plus one",
        RateTier.EmployeePlusSpouse => "employee plus spouse",
        RateTier.EmployeePlusChildren => "employee plus children",
        RateTier.Family => "family",
        _ => tier.ToString(),
    };
}
