using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using SponsorService.Repositories;

namespace SponsorService.Contracts;

public enum ContractOutcome
{
    Ok,
    Invalid,
    NotFound,
    Conflict,
}

public sealed record ContractResult<T>(ContractOutcome Outcome, IReadOnlyList<string> Errors, T? Value = default)
{
    public static ContractResult<T> Ok(T value) => new(ContractOutcome.Ok, [], value);
    public static ContractResult<T> Fail(ContractOutcome outcome, params string[] errors) => new(outcome, errors);
}

/// <summary>
/// Why a date has, or has not, billing terms from the contract. The two ways
/// of having none are not interchangeable: see
/// <c>ContractService.BillingInForceAsync</c>.
/// </summary>
public enum BillingResolutionOutcome
{
    Resolved,
    NoContractTerm,
    BillingNotEntered,
}

/// <summary>The billing terms in force on a date, and which contract period they came from.</summary>
public sealed record BillingResolution(
    BillingResolutionOutcome Outcome, BillingTerms? Billing = null, ContractTerm? ContractTerm = null);

/// <summary>What a caller may set on a sponsor policy.</summary>
public sealed record PolicyWrite
{
    public string? PolicyNumber { get; init; }
    public string? Name { get; init; }
    public string? BenefitPlanId { get; init; }
    public string? ProgramId { get; init; }
    public List<SponsorClass>? Classes { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public DateTime? EffectiveTo { get; init; }
    public bool? Active { get; init; }
}

/// <summary>What a caller may set on a contract term.</summary>
public sealed record TermWrite
{
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }
    public DateTime? RenewalDate { get; init; }
}

/// <summary>What a caller may set on a contract term's billing and underwriting.</summary>
public sealed record TermTermsWrite
{
    public BillingTerms? Billing { get; init; }
    public UnderwritingConditions? Underwriting { get; init; }
}

/// <summary>What a caller may say when moving a term.</summary>
public sealed record TermStatusWrite
{
    public ContractTermStatus Status { get; init; }
    public string? Reason { get; init; }
    public string? SignedDocumentId { get; init; }
    public DateTime? SignedOn { get; init; }
}

/// <summary>
/// Sponsor policies and contract terms
/// (docs/architecture/plan-configuration.md §9b; step 3 of the §12 build
/// order). Reads and writes are confined to the caller's tenant, which comes
/// from its token, and to a sponsor that tenant actually has.
///
/// <para>
/// The rules that live here rather than in the records are the ones that make
/// the hierarchy answerable later: a policy number is unique in its group and
/// fixed once set, the plan and program a policy is sold under cannot be
/// swapped underneath its members, contract periods do not overlap, and a term
/// cannot claim to be active before its own start date. Each of them is
/// something a caller could otherwise write and nothing downstream could
/// recover from.
/// </para>
/// </summary>
public sealed class ContractService(
    IContractStore store,
    ISponsorRepository sponsors,
    TimeProvider clock,
    ILogger<ContractService> logger)
{
    /// <summary>A policy number as §9b prints it: the group number, a dash, a sequence. Upper case, no spaces.</summary>
    private static readonly Regex PolicyNumberPattern = new(@"^[A-Z0-9][A-Z0-9\-]{0,49}$", RegexOptions.Compiled);
    private static readonly Regex ClassCodePattern = new(@"^[A-Z0-9][A-Z0-9_\-]{0,31}$", RegexOptions.Compiled);

    private DateTime Today => DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime.Date, DateTimeKind.Utc);

    // ── Policies ──────────────────────────────────────────────────

    public async Task<ContractResult<IReadOnlyList<SponsorPolicy>>> ListPoliciesAsync(
        string tenantId, string groupNumber, CancellationToken ct = default)
    {
        if (!await SponsorExistsAsync(tenantId, groupNumber, ct))
            return ContractResult<IReadOnlyList<SponsorPolicy>>.Fail(ContractOutcome.NotFound, $"No sponsor with group number '{groupNumber}'.");
        return ContractResult<IReadOnlyList<SponsorPolicy>>.Ok(await store.ListPoliciesAsync(tenantId, groupNumber, ct));
    }

    public async Task<ContractResult<SponsorPolicy>> GetPolicyAsync(
        string tenantId, string groupNumber, string policyNumber, CancellationToken ct = default)
    {
        var policy = await store.GetPolicyAsync(tenantId, SponsorPolicy.IdFor(tenantId, groupNumber, Normalize(policyNumber)), ct);
        return policy is null
            ? ContractResult<SponsorPolicy>.Fail(ContractOutcome.NotFound, $"No policy '{policyNumber}' under group '{groupNumber}'.")
            : ContractResult<SponsorPolicy>.Ok(policy);
    }

    public async Task<ContractResult<SponsorPolicy>> CreatePolicyAsync(
        ICurrentActor actor, string groupNumber, PolicyWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        if (!await SponsorExistsAsync(tenantId, groupNumber, ct))
            return ContractResult<SponsorPolicy>.Fail(ContractOutcome.NotFound, $"No sponsor with group number '{groupNumber}'.");

        var errors = new List<string>();
        var policyNumber = Normalize(write.PolicyNumber);
        if (policyNumber.Length == 0) errors.Add("A policy number is required; it is what goes on the ID card and the 834.");
        else if (!PolicyNumberPattern.IsMatch(policyNumber))
            errors.Add("A policy number is up to 50 characters of A-Z, 0-9 and '-'.");
        if (string.IsNullOrWhiteSpace(write.Name)) errors.Add("A name is required.");
        if (string.IsNullOrWhiteSpace(write.BenefitPlanId))
            errors.Add("A benefit plan is required: a policy is a plan offered under a policy number.");
        errors.AddRange(ClassErrors(write.Classes));
        errors.AddRange(DateErrors(write.EffectiveFrom, write.EffectiveTo));
        if (errors.Count > 0) return new ContractResult<SponsorPolicy>(ContractOutcome.Invalid, errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var policy = new SponsorPolicy
        {
            Id = SponsorPolicy.IdFor(tenantId, groupNumber, policyNumber),
            TenantId = tenantId,
            GroupNumber = groupNumber,
            PolicyNumber = policyNumber,
            Name = write.Name!.Trim(),
            BenefitPlanId = write.BenefitPlanId!.Trim(),
            ProgramId = string.IsNullOrWhiteSpace(write.ProgramId) ? null : write.ProgramId.Trim(),
            Classes = CanonicalClasses(write.Classes),
            EffectiveFrom = write.EffectiveFrom?.Date,
            EffectiveTo = write.EffectiveTo?.Date,
            Active = write.Active ?? true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedBy = actor.UserId,
        };

        if (await store.GetPolicyAsync(tenantId, policy.Id, ct) is not null)
            return ContractResult<SponsorPolicy>.Fail(ContractOutcome.Conflict,
                $"Group '{groupNumber}' already has a policy '{policyNumber}'.");

        await store.InsertPolicyAsync(policy, ct);
        logger.LogInformation("Created sponsor policy {PolicyNumber} under group {GroupNumber} for tenant {TenantId}",
            LogSanitizer.Sanitize(policyNumber), LogSanitizer.Sanitize(groupNumber), LogSanitizer.Sanitize(tenantId));
        return ContractResult<SponsorPolicy>.Ok(policy);
    }

    /// <summary>
    /// Changes what a policy may change: its name, its dates, whether it is
    /// offered, and its classes.
    ///
    /// <para>
    /// The policy number, the benefit plan and the program are <b>refused</b>,
    /// not ignored. §9b: a different plan, program or policy number means the
    /// old policy ends the day before renewal and a new one starts, and the
    /// members move through the renewal's enrollment. Letting one be edited in
    /// place would move every member's coverage onto a different plan without
    /// an enrollment, which is the failure the whole continue-versus-replace
    /// rule exists to prevent — and it would do it silently.
    /// </para>
    /// </summary>
    public async Task<ContractResult<SponsorPolicy>> UpdatePolicyAsync(
        ICurrentActor actor, string groupNumber, string policyNumber, PolicyWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        var id = SponsorPolicy.IdFor(tenantId, groupNumber, Normalize(policyNumber));
        if (await store.GetPolicyAsync(tenantId, id, ct) is not { } policy)
            return ContractResult<SponsorPolicy>.Fail(ContractOutcome.NotFound, $"No policy '{policyNumber}' under group '{groupNumber}'.");

        var errors = new List<string>();
        if (Changes(write.PolicyNumber, policy.PolicyNumber))
            errors.Add("A policy's number cannot change: it is on ID cards and inbound 834s. End this policy and start a new one.");
        if (Changes(write.BenefitPlanId, policy.BenefitPlanId))
            errors.Add("A policy's benefit plan cannot change: end this policy the day before the renewal and start a new one, so members move through enrollment.");
        if (write.ProgramId is not null && Changes(write.ProgramId, policy.ProgramId ?? string.Empty))
            errors.Add("A policy's program cannot change: end this policy and start a new one under the new program.");
        if (write.Name is not null && string.IsNullOrWhiteSpace(write.Name)) errors.Add("A name is required.");
        errors.AddRange(ClassErrors(write.Classes));
        errors.AddRange(DateErrors(write.EffectiveFrom ?? policy.EffectiveFrom, write.EffectiveTo ?? policy.EffectiveTo));
        if (errors.Count > 0) return new ContractResult<SponsorPolicy>(ContractOutcome.Invalid, errors);

        var expected = policy.Revision;
        policy.Name = write.Name?.Trim() ?? policy.Name;
        if (write.Classes is not null) policy.Classes = CanonicalClasses(write.Classes);
        if (write.EffectiveFrom is not null) policy.EffectiveFrom = write.EffectiveFrom.Value.Date;
        if (write.EffectiveTo is not null) policy.EffectiveTo = write.EffectiveTo.Value.Date;
        if (write.Active is not null) policy.Active = write.Active.Value;
        policy.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        policy.UpdatedBy = actor.UserId;

        if (!await store.ReplacePolicyAsync(policy, expected, ct))
            return ContractResult<SponsorPolicy>.Fail(ContractOutcome.Conflict, "The policy changed while this edit was being made; read it again.");
        return ContractResult<SponsorPolicy>.Ok(policy);
    }

    // ── Contract terms ────────────────────────────────────────────

    public async Task<ContractResult<IReadOnlyList<ContractTerm>>> ListTermsAsync(
        string tenantId, string groupNumber, CancellationToken ct = default)
    {
        if (!await SponsorExistsAsync(tenantId, groupNumber, ct))
            return ContractResult<IReadOnlyList<ContractTerm>>.Fail(ContractOutcome.NotFound, $"No sponsor with group number '{groupNumber}'.");
        return ContractResult<IReadOnlyList<ContractTerm>>.Ok(await store.ListTermsAsync(tenantId, groupNumber, ct));
    }

    /// <summary>
    /// The term to resolve <paramref name="on"/> against: the one signed or
    /// active whose period covers that date. Null where the sponsor has none,
    /// which is every sponsor today and is an answer rather than a gap — a
    /// consumer falls back on what it reads now.
    /// </summary>
    public async Task<ContractTerm?> TermInForceAsync(string tenantId, string groupNumber, DateTime on, CancellationToken ct = default) =>
        (await store.ListTermsAsync(tenantId, groupNumber, ct)).FirstOrDefault(t => t.IsInForce(on));

    /// <summary>
    /// The billing terms to bill a date on, found through the contract term in
    /// force over it (§9b, step 3d).
    ///
    /// <para>
    /// The two ways of having none are kept apart. <see cref="BillingResolutionOutcome.NoContractTerm"/>
    /// is a group that has no contract covering the date, which is every group
    /// until one is entered, and a consumer falls back on the sponsor record's
    /// own billing information. <see cref="BillingResolutionOutcome.BillingNotEntered"/>
    /// is a contract that exists and says nothing about billing — the same
    /// fallback applies, because the sponsor record is where billing lived
    /// before the contract existed and is not stale on a contract's account,
    /// but it is worth seeing separately: somebody agreed a contract and left
    /// its billing blank.
    /// </para>
    /// </summary>
    public async Task<BillingResolution> BillingInForceAsync(
        string tenantId, string groupNumber, DateTime on, CancellationToken ct = default)
    {
        var term = await TermInForceAsync(tenantId, groupNumber, on, ct);
        if (term is null) return new BillingResolution(BillingResolutionOutcome.NoContractTerm);
        return term.Billing is null
            ? new BillingResolution(BillingResolutionOutcome.BillingNotEntered, ContractTerm: term)
            : new BillingResolution(BillingResolutionOutcome.Resolved, term.Billing, term);
    }

    public async Task<ContractResult<ContractTerm>> CreateTermAsync(
        ICurrentActor actor, string groupNumber, TermWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        if (!await SponsorExistsAsync(tenantId, groupNumber, ct))
            return ContractResult<ContractTerm>.Fail(ContractOutcome.NotFound, $"No sponsor with group number '{groupNumber}'.");

        var errors = new List<string>();
        if (write.PeriodStart is null) errors.Add("A contract period needs a start date.");
        if (write.PeriodEnd is null) errors.Add("A contract period needs an end date.");
        if (write.PeriodStart is { } from && write.PeriodEnd is { } to && to.Date < from.Date)
            errors.Add("The contract period's end date must not be before its start date.");
        if (write.RenewalDate is { } renewal && write.PeriodStart is { } start && renewal.Date <= start.Date)
            errors.Add("The renewal date must be after the contract period starts.");
        if (errors.Count > 0) return new ContractResult<ContractTerm>(ContractOutcome.Invalid, errors);

        var existing = await store.ListTermsAsync(tenantId, groupNumber, ct);
        // Overlapping periods would make "the term in force on this date"
        // ambiguous, which is the one question every consumer of §9b asks. An
        // ended term is left out: it is over, and a replacement for the same
        // period is how a mistake gets corrected.
        if (existing.FirstOrDefault(t => t.Status != ContractTermStatus.Ended
                && t.PeriodStart.Date <= write.PeriodEnd!.Value.Date
                && write.PeriodStart!.Value.Date <= t.PeriodEnd.Date) is { } clash)
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Conflict,
                $"The period overlaps the term starting {clash.PeriodStart:yyyy-MM-dd}; end that one first.");

        var now = clock.GetUtcNow().UtcDateTime;
        var term = new ContractTerm
        {
            Id = ContractTerm.IdFor(tenantId, groupNumber, write.PeriodStart!.Value),
            TenantId = tenantId,
            GroupNumber = groupNumber,
            PeriodStart = write.PeriodStart.Value.Date,
            PeriodEnd = write.PeriodEnd!.Value.Date,
            RenewalDate = write.RenewalDate?.Date,
            Status = ContractTermStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = actor.UserId,
            UpdatedBy = actor.UserId,
        };

        if (await store.GetTermAsync(tenantId, term.Id, ct) is not null)
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Conflict,
                $"Group '{groupNumber}' already has a term starting {term.PeriodStart:yyyy-MM-dd}.");

        await store.InsertTermAsync(term, ct);
        logger.LogInformation("Opened a draft contract term from {PeriodStart:yyyy-MM-dd} for group {GroupNumber}, tenant {TenantId}",
            term.PeriodStart, LogSanitizer.Sanitize(groupNumber), LogSanitizer.Sanitize(tenantId));
        return ContractResult<ContractTerm>.Ok(term);
    }

    /// <summary>
    /// Enters the contract year's billing terms and the conditions the quote
    /// was written against (§9b).
    ///
    /// <para>
    /// Once the term is signed or active, the <b>agreed</b> terms are settled
    /// the way a policy's rates are: the billing method, how often the premium
    /// falls due, which day it falls due on and how long it may run late are
    /// what both sides signed, and changing them in place would move them with
    /// nothing on the record. The <b>remittance details</b> — the billing
    /// account number and the payment method — are not agreed terms but the
    /// plumbing for applying a payment, and a group that changes bank in March
    /// should not need a new contract period to say so. So those two stay
    /// editable and the rest do not.
    /// </para>
    /// </summary>
    public async Task<ContractResult<ContractTerm>> SetTermTermsAsync(
        ICurrentActor actor, string groupNumber, DateTime periodStart, TermTermsWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        var id = ContractTerm.IdFor(tenantId, groupNumber, periodStart);
        if (await store.GetTermAsync(tenantId, id, ct) is not { } term)
            return ContractResult<ContractTerm>.Fail(ContractOutcome.NotFound,
                $"Group '{groupNumber}' has no contract term starting {periodStart:yyyy-MM-dd}.");

        if (term.Status is ContractTermStatus.Ended)
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Conflict,
                $"The contract term starting {periodStart:yyyy-MM-dd} has ended; its terms cannot be changed.");

        var settled = term.Status is ContractTermStatus.Signed or ContractTermStatus.Active;
        var errors = new List<string>();
        if (settled && write.Underwriting is not null && !SameUnderwriting(write.Underwriting, term.Underwriting))
            errors.Add($"The contract term starting {periodStart:yyyy-MM-dd} is {Spell(term.Status)}, so the conditions it was written against are settled. Open a new contract period to change them.");
        if (settled && write.Billing is not null && ChangesAgreedBilling(write.Billing, term.Billing))
            errors.Add($"The contract term starting {periodStart:yyyy-MM-dd} is {Spell(term.Status)}, so the billing method, frequency, due day and grace period are settled. Open a new contract period to change them. The billing account number and payment method can still be corrected.");
        errors.AddRange(BillingErrors(write.Billing));
        errors.AddRange(UnderwritingErrors(write.Underwriting));
        if (errors.Count > 0) return new ContractResult<ContractTerm>(ContractOutcome.Invalid, errors);

        var expected = term.Revision;
        if (write.Billing is not null)
        {
            // Settled: only the remittance details move, and the agreed terms
            // are kept as signed rather than taken from the caller.
            term.Billing = settled && term.Billing is { } agreed
                ? new BillingTerms
                {
                    Method = agreed.Method,
                    Frequency = agreed.Frequency,
                    DueDay = agreed.DueDay,
                    GracePeriodDays = agreed.GracePeriodDays,
                    BillingAccountNumber = Trimmed(write.Billing.BillingAccountNumber) ?? agreed.BillingAccountNumber,
                    PaymentMethod = Trimmed(write.Billing.PaymentMethod) ?? agreed.PaymentMethod,
                }
                : new BillingTerms
                {
                    Method = write.Billing.Method,
                    Frequency = write.Billing.Frequency,
                    DueDay = write.Billing.DueDay,
                    GracePeriodDays = write.Billing.GracePeriodDays,
                    BillingAccountNumber = Trimmed(write.Billing.BillingAccountNumber),
                    PaymentMethod = Trimmed(write.Billing.PaymentMethod),
                };
        }
        if (write.Underwriting is not null)
        {
            term.Underwriting = new UnderwritingConditions
            {
                MinimumParticipationPercent = write.Underwriting.MinimumParticipationPercent,
                MinimumEmployerContributionPercent = write.Underwriting.MinimumEmployerContributionPercent,
                MinimumEnrolledEmployees = write.Underwriting.MinimumEnrolledEmployees,
                Notes = Trimmed(write.Underwriting.Notes),
            };
        }
        term.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        term.UpdatedBy = actor.UserId;

        if (!await store.ReplaceTermAsync(term, expected, ct))
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Conflict,
                "The term changed while this edit was being made; read it again.");
        return ContractResult<ContractTerm>.Ok(term);
    }

    /// <summary>
    /// Moves a term along §9b's flow. The checks worth having here rather than
    /// in a reviewer's head:
    /// <list type="bullet">
    ///   <item>signing needs the signed document, because a term marked signed
    ///   with nothing attached is a claim nobody can check;</item>
    ///   <item>a term cannot become active before its own start date, so the
    ///   record cannot say a group is covered from a day the contract does not
    ///   cover;</item>
    ///   <item>setting the status a term already has changes nothing and
    ///   succeeds, so a retried call is safe.</item>
    /// </list>
    /// </summary>
    public async Task<ContractResult<ContractTerm>> SetTermStatusAsync(
        ICurrentActor actor, string groupNumber, DateTime periodStart, TermStatusWrite write, CancellationToken ct = default)
    {
        var tenantId = actor.TenantId;
        var id = ContractTerm.IdFor(tenantId, groupNumber, periodStart);
        if (await store.GetTermAsync(tenantId, id, ct) is not { } term)
            return ContractResult<ContractTerm>.Fail(ContractOutcome.NotFound,
                $"No contract term starting {periodStart:yyyy-MM-dd} under group '{groupNumber}'.");

        var documentId = string.IsNullOrWhiteSpace(write.SignedDocumentId) ? null : write.SignedDocumentId.Trim();
        if (term.Status == write.Status)
        {
            // Already there. Attaching the document is still worth accepting,
            // so a signature recorded in two calls is not refused on the second.
            if (documentId is null || documentId == term.SignedDocumentId) return ContractResult<ContractTerm>.Ok(term);
        }
        else if (!ContractTermTransitions.IsAllowed(term.Status, write.Status))
        {
            var allowed = ContractTermTransitions.AllowedFrom(term.Status);
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Invalid,
                $"A {term.Status} term cannot become {write.Status}."
                + (allowed.Count == 0 ? " It is final." : $" It can become: {string.Join(", ", allowed)}."));
        }

        var errors = new List<string>();
        if (write.Status == ContractTermStatus.Signed && documentId is null && term.SignedDocumentId is null)
            errors.Add("Signing needs the signed agreement: give the document id it was stored under.");
        if (write.Status == ContractTermStatus.Active && term.PeriodStart.Date > Today)
            errors.Add($"The term starts on {term.PeriodStart:yyyy-MM-dd}; it cannot be active before then.");
        if (errors.Count > 0) return new ContractResult<ContractTerm>(ContractOutcome.Invalid, errors);

        var now = clock.GetUtcNow().UtcDateTime;
        var expected = term.Revision;
        if (term.Status != write.Status)
        {
            term.StatusHistory.Add(new ContractTermStatusChange
            {
                From = term.Status, To = write.Status, Reason = write.Reason?.Trim(),
                ChangedBy = actor.UserId, ChangedAt = now,
            });
            term.Status = write.Status;
            term.StatusReason = write.Reason?.Trim();
        }
        if (documentId is not null)
        {
            term.SignedDocumentId = documentId;
            term.SignedOn = (write.SignedOn ?? now).Date;
        }
        term.UpdatedAt = now;
        term.UpdatedBy = actor.UserId;

        if (!await store.ReplaceTermAsync(term, expected, ct))
            return ContractResult<ContractTerm>.Fail(ContractOutcome.Conflict, "The term changed while this move was being made; read it again.");
        return ContractResult<ContractTerm>.Ok(term);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private async Task<bool> SponsorExistsAsync(string tenantId, string groupNumber, CancellationToken ct)
    {
        _ = ct;
        return !string.IsNullOrWhiteSpace(groupNumber) && await sponsors.ExistsAsync(tenantId, groupNumber);
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Spell(ContractTermStatus status) =>
        status == ContractTermStatus.InUnderwriting ? "in underwriting" : status.ToString().ToLowerInvariant();

    /// <summary>
    /// Whether the caller is changing one of the billing terms both sides
    /// signed, as opposed to the remittance details. A caller that sends the
    /// whole record back unchanged is not changing anything, so a client that
    /// round-trips it still works after signature.
    /// </summary>
    private static bool ChangesAgreedBilling(BillingTerms stated, BillingTerms? stored) =>
        stored is null
            ? true
            : stated.Method != stored.Method
              || stated.Frequency != stored.Frequency
              || stated.DueDay != stored.DueDay
              || stated.GracePeriodDays != stored.GracePeriodDays;

    private static bool SameUnderwriting(UnderwritingConditions stated, UnderwritingConditions? stored) =>
        stored is not null
        && stated.MinimumParticipationPercent == stored.MinimumParticipationPercent
        && stated.MinimumEmployerContributionPercent == stored.MinimumEmployerContributionPercent
        && stated.MinimumEnrolledEmployees == stored.MinimumEnrolledEmployees;

    private static IEnumerable<string> BillingErrors(BillingTerms? billing)
    {
        if (billing is null) yield break;
        if (billing.DueDay is < 1 or > 31)
            yield return "The premium's due day must be a day of the month, 1 to 31.";
        // The same range the sponsor record's flat grace period allowed, so
        // that moving the field onto the contract does not change what a
        // customer may enter.
        if (billing.GracePeriodDays is < 0 or > 90)
            yield return "A grace period must be between 0 and 90 days.";
    }

    private static IEnumerable<string> UnderwritingErrors(UnderwritingConditions? underwriting)
    {
        if (underwriting is null) yield break;
        foreach (var (percent, what) in new[]
                 {
                     (underwriting.MinimumParticipationPercent, "participation"),
                     (underwriting.MinimumEmployerContributionPercent, "employer contribution"),
                 })
        {
            if (percent is { } value && value is < 0 or > 100)
                yield return $"The minimum {what} must be a percentage between 0 and 100.";
        }
        if (underwriting.MinimumEnrolledEmployees is { } enrolled && enrolled < 0)
            yield return "The fewest enrolled employees cannot be negative.";
    }

    /// <summary>Whether the caller stated a value and it differs from what is stored.</summary>
    private static bool Changes(string? stated, string stored) =>
        !string.IsNullOrWhiteSpace(stated) && !string.Equals(Normalize(stated), Normalize(stored), StringComparison.Ordinal);

    private static IEnumerable<string> DateErrors(DateTime? from, DateTime? to) =>
        from is { } f && to is { } t && t.Date < f.Date
            ? ["The policy's end date must not be before the date it starts being offered."]
            : [];

    private static IEnumerable<string> ClassErrors(List<SponsorClass>? classes)
    {
        if (classes is null) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in classes)
        {
            var code = Normalize(c.Code);
            if (code.Length == 0) { yield return "Every class needs a code."; continue; }
            if (!ClassCodePattern.IsMatch(code))
                yield return $"Class code '{c.Code}' is not up to 32 characters of A-Z, 0-9, '-' and '_'.";
            if (string.IsNullOrWhiteSpace(c.Name)) yield return $"Class '{code}' needs a name.";
            // Two classes with one code would give a class-level setting two
            // nodes under the same scope id.
            if (!seen.Add(code)) yield return $"Class code '{code}' is listed twice.";
        }
    }

    private static List<SponsorClass> CanonicalClasses(List<SponsorClass>? classes) =>
        classes?.Select(c => new SponsorClass
        {
            Code = Normalize(c.Code),
            Name = c.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(c.Description) ? null : c.Description.Trim(),
            Active = c.Active,
        }).ToList() ?? [];
}
