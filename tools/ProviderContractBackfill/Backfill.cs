using MongoDB.Bson;
using EngineDomain = CloudHealthOffice.FeeScheduleEngine.Domain;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;
using LegalModels = ProviderContractsService.Models;

namespace ProviderContractBackfill;

/// <summary>
/// Builds one canonical contract per provider-and-plan out of the four places a
/// provider's rates live today (ADR 016 phase 3).
///
/// <para>
/// Identity and scope come from the legal contract in provider-contracts-service.
/// Terms come from its <c>FfsRateConfig</c> and <c>CapitationRateConfig</c>
/// children, and from the engine contract for the same NPI and plan — its
/// <c>ContractLines</c> become scoped terms and its <c>FeeScheduleId</c> the
/// last-resort one. An engine contract with no legal contract becomes a
/// canonical contract flagged <c>engine-only</c> for review.
/// </para>
///
/// <para>
/// Nothing is guessed. Where the stores disagree — two rates that both price
/// every line, a legal and an engine contract naming different schedules, a
/// hybrid contract with no record of which services are capitated, a term
/// pointing at a schedule that does not exist — the provider-and-plan is
/// blocked, reported, and left exactly as it is. Those are the conflicts the
/// phase 5 cutover is gated on, and a backfill that resolved them by picking
/// one would change what a provider is paid without anyone deciding to.
/// </para>
/// </summary>
public sealed class Backfill(Stores stores, Action<string> log)
{
    /// <summary>
    /// The plan for these tenants, or for every tenant in the database when none
    /// are named. Read-only: nothing is written until <c>apply --execute</c>.
    /// </summary>
    public async Task<BackfillPlan> PlanAsync(IReadOnlyCollection<string> tenants)
    {
        var subjects = tenants.Count > 0
            ? tenants.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList()
            : await stores.TenantsAsync();

        var plan = new BackfillPlan
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            DatabaseName = stores.DatabaseName,
            Tenants = subjects,
        };

        foreach (var tenantId in subjects)
        {
            var rows = await PlanTenantAsync(tenantId);
            plan.Contracts.AddRange(rows);
            log($"  {tenantId}: " + string.Join(", ", rows
                .GroupBy(r => r.Decision)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}")));
        }

        return plan;
    }

    private async Task<List<PlannedContract>> PlanTenantAsync(string tenantId)
    {
        var legalContracts = await stores.LegalContractsAsync(tenantId);
        var engineContracts = await stores.EngineContractsAsync(tenantId);
        var scheduleIds = await stores.FeeScheduleIdsAsync(tenantId);
        var ffsConfigs = await stores.FfsRateConfigsAsync(tenantId);
        var capitationConfigs = await stores.CapitationRateConfigsAsync(tenantId);

        var engineById = engineContracts.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var claimedEngineIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<PlannedContract>();

        // Two legal contracts covering the same provider and plan would both
        // project onto one engine id, so neither can be made canonical without
        // choosing between them.
        var byProviderAndPlan = legalContracts
            .SelectMany(c => PlanIdsOf(c).Select(planId => (Contract: c, PlanId: planId)))
            .GroupBy(x => (x.Contract.ProviderNPI, x.PlanId));

        var contested = byProviderAndPlan
            .Where(g => g.Select(x => x.Contract.Id).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Contract.Id).Distinct(StringComparer.Ordinal).ToList());

        foreach (var legal in legalContracts.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            foreach (var planId in PlanIdsOf(legal))
            {
                var canonicalId = EngineModels.ProviderContract.MakeId(tenantId, legal.ProviderNPI, planId);
                engineById.TryGetValue(canonicalId, out var engine);
                if (engine is not null)
                    claimedEngineIds.Add(canonicalId);

                var row = FromLegal(
                    tenantId, legal, planId, canonicalId, engine, scheduleIds,
                    ffsConfigs.Where(d => ParentContractId(d) == legal.Id).ToList(),
                    capitationConfigs.Where(d => ParentContractId(d) == legal.Id).ToList());

                if (contested.TryGetValue((legal.ProviderNPI, planId), out var ids))
                {
                    Block(row,
                        $"provider {legal.ProviderNPI} on plan {planId} is covered by more than one legal " +
                        $"contract ({string.Join(", ", ids)}); one canonical contract cannot be built from " +
                        "both, so the contracts must be narrowed or one of them terminated");
                }

                rows.Add(row);
            }

            if (legal.PlanIds.All(string.IsNullOrWhiteSpace) || legal.PlanIds.Count == 0)
            {
                // PlanIdsOf returned nothing, so no row was added above.
                rows.Add(NoPlanIds(tenantId, legal));
            }
        }

        // Engine contracts with no legal agreement: seeded or hand-made data
        // that is nonetheless what the engine prices from today.
        foreach (var engine in engineContracts
                     .Where(c => !claimedEngineIds.Contains(c.Id))
                     .OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            rows.Add(FromEngineOnly(tenantId, engine, scheduleIds));
        }

        return rows;
    }

    // ── A legal contract, on one plan ─────────────────────────────────

    private static PlannedContract FromLegal(
        string tenantId,
        LegalModels.ProviderContract legal,
        string planId,
        string canonicalId,
        EngineModels.ProviderContract? engine,
        HashSet<string> scheduleIds,
        List<BsonDocument> ffsConfigs,
        List<BsonDocument> capitationConfigs)
    {
        var row = new PlannedContract
        {
            TenantId = tenantId,
            CanonicalId = canonicalId,
            LegalContractId = legal.Id,
            ContractNumber = legal.ContractNumber,
            ProviderNpi = legal.ProviderNPI,
            PlanId = planId,
            Origin = "legal",
            Replaces = engine is not null,
        };

        // The legal contract may already carry its own rate sheet, written
        // through the terms API. That is the finished shape, so it wins outright
        // and nothing is derived.
        if (legal.ReimbursementTerms.Count > 0)
        {
            row.TermSources.Add("the legal contract's own reimbursement terms");
            return Finish(row, legal, planId, legal.ReimbursementTerms.ToList(), engine, scheduleIds);
        }

        var terms = new List<EngineModels.ReimbursementTerm>();

        // Scoped terms first, so a carve-out is reachable: the engine's contract
        // lines route procedure-code ranges to other schedules.
        if (engine is not null)
        {
            foreach (var line in engine.ContractLines)
            {
                terms.Add(new EngineModels.ReimbursementTerm
                {
                    Description = $"Engine contract line {line.ProcedureCodeFrom}–{line.ProcedureCodeTo}",
                    Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                    FeeScheduleId = line.FeeScheduleId,
                    Scope = new EngineModels.ReimbursementTermScope
                    {
                        ProcedureCodeFrom = line.ProcedureCodeFrom,
                        ProcedureCodeTo = line.ProcedureCodeTo,
                    },
                });
            }

            if (engine.ContractLines.Count > 0)
                row.TermSources.Add("the engine contract's contract lines");
        }

        var defaults = new List<EngineModels.ReimbursementTerm>();

        foreach (var config in ffsConfigs.OrderBy(EffectiveDateOf))
        {
            var feeScheduleId = String(config, "FeeScheduleId");
            if (string.IsNullOrWhiteSpace(feeScheduleId))
            {
                Block(row,
                    $"fee-for-service rate config {String(config, "RateConfigNumber") ?? config.GetValue("_id", "?")} " +
                    "names no fee schedule, so there is no rate to make canonical");
                continue;
            }

            defaults.Add(new EngineModels.ReimbursementTerm
            {
                Description = String(config, "RateConfigNumber") is { Length: > 0 } number
                    ? $"Fee-for-service rate config {number}"
                    : "Fee-for-service rate config",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = feeScheduleId,
                Multiplier = Decimal(config, "FeeSchedulePercentage") ?? 1.0m,
                EffectiveDate = EffectiveDateOf(config),
                TermDate = Date(config, "TerminationDate"),
                ModifierRules = new EngineModels.TermModifierRules
                {
                    MultipleProcedureReduction = Bool(config, "MultipleProcedureReduction") ?? true,
                    BilateralAdjustment = Bool(config, "BilateralAdjustment") ?? true,
                },
            });

            row.TermSources.Add("a fee-for-service rate config");
        }

        foreach (var config in capitationConfigs.OrderBy(EffectiveDateOf))
        {
            defaults.Add(new EngineModels.ReimbursementTerm
            {
                Description = String(config, "RateConfigNumber") is { Length: > 0 } number
                    ? $"Capitation rate config {number}"
                    : "Capitation rate config",
                Method = EngineModels.ReimbursementMethod.Capitation,
                CapitationRateConfigId = config.GetValue("_id", BsonNull.Value).IsString
                    ? config["_id"].AsString
                    : null,
                EffectiveDate = EffectiveDateOf(config),
                TermDate = Date(config, "TerminationDate"),
            });

            row.TermSources.Add("a capitation rate config");
        }

        // The engine contract's own default schedule is a rate too, and it is
        // the one that prices claims today.
        if (engine is not null && !string.IsNullOrWhiteSpace(engine.FeeScheduleId))
        {
            defaults.Add(new EngineModels.ReimbursementTerm
            {
                Description = "Engine contract default fee schedule",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = engine.FeeScheduleId,
            });

            row.TermSources.Add("the engine contract's default fee schedule");
        }

        // Every default term prices every line, so there can be only one for a
        // given span of dates. Where two name the same schedule at the same
        // multiplier they are the same rate recorded twice and one is kept;
        // where they differ, the stores disagree about what this provider is
        // paid and only a person can say which is right.
        var distinctDefaults = DistinctDefaults(defaults);
        if (distinctDefaults.Count > 1 && OverlapInDates(distinctDefaults))
        {
            Block(row,
                "more than one rate prices every claim line for the same dates (" +
                string.Join("; ", distinctDefaults.Select(Describe)) +
                "); a canonical contract can hold only one default rate, so the stores must be " +
                "reconciled before this provider can be made canonical");
        }
        if (legal.PaymentMethodology == LegalModels.PaymentMethodology.Hybrid
                 && distinctDefaults.Any(t => t.Method == EngineModels.ReimbursementMethod.Capitation)
                 && distinctDefaults.Any(t => t.Method != EngineModels.ReimbursementMethod.Capitation))
        {
            Block(row,
                "this is a hybrid contract — some services capitated, the rest fee-for-service — and " +
                "nothing records which services are which, so the capitated scope has to be entered on " +
                "the contract's terms before it can be made canonical");
        }

        terms.AddRange(distinctDefaults);
        return Finish(row, legal, planId, terms, engine, scheduleIds);
    }

    private static PlannedContract Finish(
        PlannedContract row,
        LegalModels.ProviderContract legal,
        string planId,
        List<EngineModels.ReimbursementTerm> terms,
        EngineModels.ProviderContract? engine,
        HashSet<string> scheduleIds)
    {
        if (terms.Count == 0 && row.Conflicts.Count == 0)
        {
            row.Decision = PlanDecision.Skipped;
            row.Notes.Add(
                "the contract has no rates anywhere — no rate config children, and no engine contract " +
                "naming a fee schedule — so there is nothing to make canonical");
            return row;
        }

        var contract = new EngineModels.ProviderContract
        {
            Id = row.CanonicalId,
            TenantId = row.TenantId,
            ProviderNpi = legal.ProviderNPI,
            PlanId = planId,
            NetworkStatus = NetworkStatusOf(legal, row),
            EffectiveDate = legal.EffectiveDate,
            TermDate = legal.TerminationDate,
            ReimbursementTerms = terms,
            FeeScheduleId = string.Empty,
            ContractLines = [],
            LegalContractId = legal.Id,
            ContractNumber = legal.ContractNumber,
            LineOfBusiness = legal.LineOfBusiness.ToString(),
            PlanIds = legal.PlanIds.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList(),
            Origin = "legal",
            // Carried from the engine contract, which is where the provision is
            // recorded today; the legal contract has no field for it yet.
            LesserOfBilledCharges = engine?.LesserOfBilledCharges ?? false,
        };

        row.Contract = contract;

        if (legal.Status != LegalModels.ProviderContractStatus.Active)
        {
            Review(row,
                $"the legal contract is {legal.Status}, not Active; the canonical contract is written " +
                "but the cutover must wait until the contract itself is active");
        }

        CheckSchedules(row, terms, scheduleIds);
        CheckSheet(row, terms);
        Decide(row);
        return row;
    }

    // ── An engine contract with no legal agreement ────────────────────

    private static PlannedContract FromEngineOnly(
        string tenantId, EngineModels.ProviderContract engine, HashSet<string> scheduleIds)
    {
        var row = new PlannedContract
        {
            TenantId = tenantId,
            CanonicalId = engine.Id,
            LegalContractId = null,
            ContractNumber = engine.ContractNumber ?? string.Empty,
            ProviderNpi = engine.ProviderNpi,
            PlanId = engine.PlanId,
            Origin = "engine-only",
            Replaces = true,
        };

        var terms = engine.ReimbursementTerms.Count > 0
            ? engine.ReimbursementTerms.ToList()
            : BuildTermsFromLegacyFields(engine, row);

        if (terms.Count == 0)
        {
            row.Decision = PlanDecision.Skipped;
            row.Notes.Add("the engine contract names no fee schedule and no contract lines, so it prices nothing");
            return row;
        }

        row.Contract = new EngineModels.ProviderContract
        {
            Id = engine.Id,
            TenantId = tenantId,
            ProviderNpi = engine.ProviderNpi,
            GroupTin = engine.GroupTin,
            PlanId = engine.PlanId,
            NetworkStatus = engine.NetworkStatus,
            EffectiveDate = engine.EffectiveDate,
            TermDate = engine.TermDate,
            ReimbursementTerms = terms,
            FeeScheduleId = string.Empty,
            ContractLines = [],
            LegalContractId = null,
            ContractNumber = engine.ContractNumber,
            LineOfBusiness = engine.LineOfBusiness,
            PlanIds = engine.PlanIds,
            Origin = "engine-only",
            LesserOfBilledCharges = engine.LesserOfBilledCharges,
            Revision = engine.Revision,
        };

        Review(row,
            $"this contract prices claims today but has no legal agreement behind it in " +
            $"provider-contracts-service; it is made canonical as engine-only and somebody has to " +
            "decide whether it is real");

        CheckSchedules(row, terms, scheduleIds);
        CheckSheet(row, terms);
        Decide(row);
        return row;
    }

    private static List<EngineModels.ReimbursementTerm> BuildTermsFromLegacyFields(
        EngineModels.ProviderContract engine, PlannedContract row)
    {
        var terms = new List<EngineModels.ReimbursementTerm>();

        foreach (var line in engine.ContractLines)
        {
            terms.Add(new EngineModels.ReimbursementTerm
            {
                Description = $"Engine contract line {line.ProcedureCodeFrom}–{line.ProcedureCodeTo}",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = line.FeeScheduleId,
                Scope = new EngineModels.ReimbursementTermScope
                {
                    ProcedureCodeFrom = line.ProcedureCodeFrom,
                    ProcedureCodeTo = line.ProcedureCodeTo,
                },
            });
        }

        if (engine.ContractLines.Count > 0)
            row.TermSources.Add("the engine contract's contract lines");

        if (!string.IsNullOrWhiteSpace(engine.FeeScheduleId))
        {
            terms.Add(new EngineModels.ReimbursementTerm
            {
                Description = "Engine contract default fee schedule",
                Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
                FeeScheduleId = engine.FeeScheduleId,
            });
            row.TermSources.Add("the engine contract's default fee schedule");
        }

        return terms;
    }

    // ── Checks ────────────────────────────────────────────────────────

    /// <summary>
    /// Every schedule a term names must exist for the tenant. The engine pends a
    /// line whose term names a missing schedule, so writing one would turn a
    /// paying contract into a pending one.
    /// </summary>
    private static void CheckSchedules(
        PlannedContract row, List<EngineModels.ReimbursementTerm> terms, HashSet<string> scheduleIds)
    {
        // An empty schedule store means the tool is pointed at a database whose
        // schedules live elsewhere; saying "every schedule is missing" would be
        // noise, not a finding.
        if (scheduleIds.Count == 0)
        {
            Review(row,
                "this tenant has no fee schedules in the engine's schedule store, so the schedules the " +
                "terms name could not be checked");
            return;
        }

        var missing = terms
            .SelectMany(t => new[] { t.FeeScheduleId, t.BaseScheduleId })
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Where(id => !scheduleIds.Contains(id!))
            .ToList();

        if (missing.Count > 0)
        {
            Block(row,
                $"the terms name fee schedule(s) this tenant does not have ({string.Join(", ", missing)}); " +
                "the engine pends a line whose term names a missing schedule, so writing this would stop " +
                "the provider's claims from paying");
        }
    }

    /// <summary>
    /// The rate sheet has to be one provider-contracts-service would accept: no
    /// term the engine cannot price, no duplicate ids, and no term an earlier
    /// one always answers before.
    /// </summary>
    private static void CheckSheet(PlannedContract row, List<EngineModels.ReimbursementTerm> terms)
    {
        foreach (var error in LegalModels.ReimbursementTermSheet.Validate(terms))
            Block(row, $"the rate sheet built from these stores is not valid: {error}");
    }

    private static EngineDomain.NetworkStatus NetworkStatusOf(
        LegalModels.ProviderContract legal, PlannedContract row)
    {
        if (legal.NetworkStatus == LegalModels.NetworkParticipationStatus.TieredException)
        {
            Review(row,
                "network participation is TieredException, which the engine's network status has no value " +
                "for; the canonical contract says in-network and the tiering has to be carried by the " +
                "terms' scopes");
        }

        return legal.NetworkStatus switch
        {
            LegalModels.NetworkParticipationStatus.Participating => EngineDomain.NetworkStatus.InNetwork,
            LegalModels.NetworkParticipationStatus.NonParticipating => EngineDomain.NetworkStatus.OutOfNetwork,
            LegalModels.NetworkParticipationStatus.TieredException => EngineDomain.NetworkStatus.InNetwork,
            _ => EngineDomain.NetworkStatus.Unknown,
        };
    }

    private static PlannedContract NoPlanIds(string tenantId, LegalModels.ProviderContract legal)
    {
        var row = new PlannedContract
        {
            TenantId = tenantId,
            CanonicalId = string.Empty,
            LegalContractId = legal.Id,
            ContractNumber = legal.ContractNumber,
            ProviderNpi = legal.ProviderNPI,
            PlanId = string.Empty,
            Origin = "legal",
        };

        Block(row,
            $"the legal contract names no plan ids, so it covers every {legal.LineOfBusiness} plan; the " +
            "engine looks a contract up by one plan, so the plans have to be named on the contract — or " +
            "the backfill given a plan catalogue — before this provider can be made canonical");

        row.Decision = PlanDecision.Blocked;
        return row;
    }

    // ── Decisions ─────────────────────────────────────────────────────

    private static void Block(PlannedContract row, string conflict)
    {
        row.Conflicts.Add(conflict);
        row.Decision = PlanDecision.Blocked;
    }

    private static void Review(PlannedContract row, string note) => row.Notes.Add(note);

    private static void Decide(PlannedContract row)
    {
        if (row.Conflicts.Count > 0)
        {
            row.Decision = PlanDecision.Blocked;
            // A blocked row is not written, so the document on it would only
            // suggest otherwise.
            row.Contract = null;
            return;
        }

        row.Decision = row.Notes.Count > 0 ? PlanDecision.Review : PlanDecision.Ready;
    }

    // ── Default-term reconciliation ───────────────────────────────────

    /// <summary>
    /// The distinct default rates among <paramref name="defaults"/>.
    ///
    /// <para>
    /// The same rate is often recorded in two stores — a fee-for-service rate
    /// config and the engine contract's default schedule, naming the same
    /// schedule at the same multiplier. That is one rate, and keeping both would
    /// report a disagreement that is not there. Anything that is not the same
    /// rate stays, so the caller can see that the stores really do disagree.
    /// </para>
    ///
    /// <para>
    /// A duplicate is only dropped when the one that is kept is in force for at
    /// least as long, so collapsing them can never leave a gap where no rate
    /// prices. Of the pair that means the one with the wider date window wins,
    /// whichever store it came from.
    /// </para>
    /// </summary>
    private static List<EngineModels.ReimbursementTerm> DistinctDefaults(
        List<EngineModels.ReimbursementTerm> defaults)
    {
        var kept = new List<EngineModels.ReimbursementTerm>();

        foreach (var term in defaults)
        {
            var duplicate = kept.FindIndex(k => SameRate(k, term) && Overlap(k, term));
            if (duplicate < 0)
            {
                kept.Add(term);
                continue;
            }

            // Keep whichever is in force for longer; a tie keeps the first,
            // which is the rate config rather than the engine's copy of it.
            if (Covers(term, kept[duplicate]) && !Covers(kept[duplicate], term))
                kept[duplicate] = term;
        }

        return kept;
    }

    /// <summary>
    /// Whether these two terms pay the same thing: the same method from the same
    /// schedules at the same rate, with the same modifier treatment. Dates are
    /// not part of it — a rate recorded in two stores often carries one store's
    /// effective date and not the other's — and are compared separately.
    /// </summary>
    private static bool SameRate(EngineModels.ReimbursementTerm a, EngineModels.ReimbursementTerm b)
        => a.Method == b.Method
           && string.Equals(a.FeeScheduleId, b.FeeScheduleId, StringComparison.Ordinal)
           && string.Equals(a.BaseScheduleId, b.BaseScheduleId, StringComparison.Ordinal)
           && a.Multiplier == b.Multiplier
           && a.Percent == b.Percent
           && SameModifierRules(a.ModifierRules, b.ModifierRules);

    /// <summary>
    /// Whether two terms treat modifiers alike. No rules at all is the engine's
    /// default, which is both adjustments on, so it equals rules that say so.
    /// </summary>
    private static bool SameModifierRules(
        EngineModels.TermModifierRules? a, EngineModels.TermModifierRules? b)
        => (a?.MultipleProcedureReduction ?? true) == (b?.MultipleProcedureReduction ?? true)
           && (a?.BilateralAdjustment ?? true) == (b?.BilateralAdjustment ?? true);

    private static bool Overlap(EngineModels.ReimbursementTerm a, EngineModels.ReimbursementTerm b)
        => (a.EffectiveDate ?? DateTime.MinValue) <= (b.TermDate ?? DateTime.MaxValue)
           && (b.EffectiveDate ?? DateTime.MinValue) <= (a.TermDate ?? DateTime.MaxValue);

    /// <summary>Whether <paramref name="wider"/> is in force for the whole of <paramref name="inner"/>.</summary>
    private static bool Covers(EngineModels.ReimbursementTerm wider, EngineModels.ReimbursementTerm inner)
        => (wider.EffectiveDate ?? DateTime.MinValue) <= (inner.EffectiveDate ?? DateTime.MinValue)
           && (wider.TermDate ?? DateTime.MaxValue) >= (inner.TermDate ?? DateTime.MaxValue);

    /// <summary>
    /// Whether any two of these default rates are in force on the same day. Two
    /// that are not — a rate that ended in May and its replacement from June —
    /// are a rate history, not a disagreement.
    /// </summary>
    private static bool OverlapInDates(List<EngineModels.ReimbursementTerm> terms)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            for (var j = i + 1; j < terms.Count; j++)
            {
                var aFrom = terms[i].EffectiveDate ?? DateTime.MinValue;
                var aTo = terms[i].TermDate ?? DateTime.MaxValue;
                var bFrom = terms[j].EffectiveDate ?? DateTime.MinValue;
                var bTo = terms[j].TermDate ?? DateTime.MaxValue;

                if (aFrom <= bTo && bFrom <= aTo)
                    return true;
            }
        }

        return false;
    }

    private static string Describe(EngineModels.ReimbursementTerm term)
        => term.Method switch
        {
            EngineModels.ReimbursementMethod.Capitation => "capitation",
            EngineModels.ReimbursementMethod.PercentOfMedicare =>
                $"{term.Percent:P2} of Medicare schedule {term.BaseScheduleId}",
            _ => term.Multiplier == 1.0m
                ? $"fee schedule {term.FeeScheduleId}"
                : $"{term.Multiplier:P2} of fee schedule {term.FeeScheduleId}",
        };

    // ── Reading a rate config document ────────────────────────────────

    private static IEnumerable<string> PlanIdsOf(LegalModels.ProviderContract legal)
        => legal.PlanIds
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string? ParentContractId(BsonDocument document) => String(document, "ContractId");

    private static DateTime EffectiveDateOf(BsonDocument document)
        => Date(document, "EffectiveDate") ?? DateTime.MinValue;

    private static string? String(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;

    private static decimal? Decimal(BsonDocument document, string name)
    {
        if (!document.TryGetValue(name, out var value))
            return null;

        return value.BsonType switch
        {
            BsonType.Decimal128 => (decimal)value.AsDecimal128,
            BsonType.Double => (decimal)value.AsDouble,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.String when decimal.TryParse(value.AsString, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? Bool(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsBoolean ? value.AsBoolean : null;

    private static DateTime? Date(BsonDocument document, string name)
        => document.TryGetValue(name, out var value) && value.IsValidDateTime
            ? value.ToUniversalTime()
            : null;
}
