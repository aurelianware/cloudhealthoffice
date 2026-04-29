using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using static BenefitPlanService.Services.FhirExtensionBuilder;

namespace BenefitPlanService.Services;

/// <summary>
/// Hand-built FHIR R4 InsurancePlan projector (capability BP 5.8).
/// Pattern mirrors provider-service's <c>FhirPractitionerProjector</c>:
/// stateless, deterministic, no Hl7.Fhir.R4 dependency.
/// </summary>
public sealed class FhirInsurancePlanProjector : IFhirInsurancePlanProjector
{
    // Plan-Net IG 1.1.0 cost.qualifiers codes (Decision 9). The 2-element
    // array shape matches the Plan-Net example bundle; if conformance
    // testing rejects this shape, fall back to one cost entry per single
    // qualifier (4→8 cost entries per benefit).
    private const string QualifierInNetwork    = "in-network";
    private const string QualifierOutOfNetwork = "out-of-network";
    private const string QualifierCopay        = "copay";
    private const string QualifierCoinsurance  = "coinsurance";

    private readonly IFhirEndpointProjector _endpointProjector;

    public FhirInsurancePlanProjector()
        : this(new FhirEndpointProjector())
    {
    }

    public FhirInsurancePlanProjector(IFhirEndpointProjector endpointProjector)
    {
        _endpointProjector = endpointProjector
            ?? throw new ArgumentNullException(nameof(endpointProjector));
    }

    public JsonObject? Project(BenefitPlan plan) => Project(plan, networks: null, acaLimits: null);

    public JsonObject? Project(
        BenefitPlan plan,
        IReadOnlyList<OrganizationLookupResult>? networks,
        AcaLimits? acaLimits)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Non-Active versions don't get a public projection. Drafts /
        // Superseded versions exist for audit and amendment workflows
        // only — the FHIR surface is the public face of the head
        // Published version, mirroring Provider 5.7's stance.
        if (plan.VersionState != PlanVersionState.Published) return null;

        var status = ResolveStatus(plan);
        if (status is null) return null;

        var resource = new JsonObject
        {
            ["resourceType"] = "InsurancePlan",
            ["id"] = plan.PlanId,
            ["status"] = status,
        };

        // ── Identifier (PlanId) — Decision 6 ─────────────────────────
        resource["identifier"] = new JsonArray
        {
            new JsonObject
            {
                ["use"] = "official",
                ["system"] = ChoBenefitPlanFhirUrls.PlanIdSystem,
                ["value"] = plan.PlanId,
            }
        };

        // ── type (Decision 8a — two codings) ─────────────────────────
        resource["type"] = new JsonArray { BuildTypeConcept(plan) };

        // ── name ─────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(plan.PlanName))
        {
            resource["name"] = plan.PlanName;
        }

        // ── period ───────────────────────────────────────────────────
        var period = new JsonObject
        {
            ["start"] = ToFhirDate(plan.EffectiveDate),
        };
        if (plan.TerminationDate.HasValue)
        {
            period["end"] = ToFhirDate(plan.TerminationDate.Value);
        }
        resource["period"] = period;

        // ── ownedBy (display-only — Decision 12) ─────────────────────
        if (!string.IsNullOrEmpty(plan.Payer))
        {
            resource["ownedBy"] = new JsonObject { ["display"] = plan.Payer };
        }

        // ── endpoint (BP 5.9 — Plan Documents → Reference(Endpoint)) ─
        // One Reference per projectable PlanDocumentReference, ordered
        // per Decision 8 (SBC, EOC, Formulary, SPD, MRF, Other; within
        // type, EffectiveDate desc, then Id). Internal
        // documentreference/{id} entries are skipped — Endpoints require
        // an external address. See fhir-endpoint-projection.md.
        resource["endpoint"] = BuildEndpointReferences(plan);

        // ── network[] (top-level — Decision 10 site 1) ──────────────
        var topLevelNetworks = BuildNetworkReferences(plan.NetworkTiers, networks);
        if (topLevelNetworks.Count > 0)
        {
            resource["network"] = topLevelNetworks;
        }

        // ── coverage[] (one per benefit category) ───────────────────
        var coverage = BuildCoverage(plan, topLevelNetworks);
        if (coverage.Count > 0)
        {
            resource["coverage"] = coverage;
        }

        // ── plan[] (one per NetworkTier) ────────────────────────────
        var planArray = BuildPlans(plan, networks, acaLimits);
        if (planArray.Count > 0)
        {
            resource["plan"] = planArray;
        }

        // ── extension[] (CHO custom extensions — Decision 13) ───────
        var extensions = BuildTopLevelExtensions(plan);
        if (extensions.Count > 0)
        {
            resource["extension"] = extensions;
        }

        // ── meta ─────────────────────────────────────────────────────
        resource["meta"] = new JsonObject
        {
            ["lastUpdated"] = ToFhirInstant(ResolveLastUpdated(plan)),
            ["profile"] = new JsonArray(
                ChoBenefitPlanFhirUrls.UsCoreInsurancePlanProfile,
                ChoBenefitPlanFhirUrls.PlanNetInsurancePlanProfile),
        };

        return resource;
    }

    // ── status resolution (P4: derived) ─────────────────────────────────

    /// <summary>
    /// Map the plan's lifecycle to a FHIR <c>publication-status</c> code.
    /// Active when the head Published version's effective window contains
    /// "now"; retired when the plan has been terminated. Pre-effective
    /// drafts are filtered out before this method is called.
    /// </summary>
    private static string? ResolveStatus(BenefitPlan plan)
    {
        var now = DateTime.UtcNow;
        var effective = ToUtc(plan.EffectiveDate);
        var termination = plan.TerminationDate.HasValue ? ToUtc(plan.TerminationDate.Value) : (DateTime?)null;

        if (effective > now)
        {
            // Future-effective — projecting before the plan starts is
            // not part of the BP 5.8 contract. A future capability could
            // emit "draft" but that conflicts with "non-Active versions
            // don't project" (Provider 5.7 stance).
            return null;
        }

        if (termination.HasValue && termination.Value < now)
        {
            return "retired";
        }

        return "active";
    }

    // ── type concept (Decision 8a — two codings) ────────────────────────

    private static JsonObject BuildTypeConcept(BenefitPlan plan)
    {
        // First coding: HL7 standard insurance-plan-type. CHO defaults to
        // "medical" for Phase 1 — every authored BenefitPlan today carries
        // medical coverage. Dental-only / vision-only / drug-only plan
        // discrimination is a future capability that introduces a
        // BenefitPlan.CoverageScope or equivalent. Documenting the
        // deferred discrimination logic in
        // docs/architecture/fhir-insuranceplan-projection.md.
        var standardCoding = Coding(
            ChoBenefitPlanFhirUrls.InsurancePlanTypeSystem,
            "medical",
            "Medical");

        // Second coding: CHO product shape. Plan authors set this; it's
        // the right place for HMO/PPO/HDHP semantics that the standard
        // value set doesn't cover.
        var productCode = plan.PlanType.ToString();
        var productCoding = Coding(
            ChoBenefitPlanFhirUrls.PlanProductShapeSystem,
            productCode,
            productCode);

        return CodeableConcept(new[] { standardCoding, productCoding }, text: productCode);
    }

    // ── endpoint references (BP 5.9) ────────────────────────────────────

    /// <summary>
    /// Build the <c>endpoint[]</c> array for the projected InsurancePlan.
    /// Defers projectability + ordering to
    /// <see cref="IFhirEndpointProjector.OrderedProjectableDocuments(BenefitPlan)"/>
    /// so the Reference shape and the Endpoint resource always agree.
    ///
    /// <para>
    /// References are emitted as <c>{"reference":"Endpoint/{id}"}</c> per
    /// the BP 5.8 Reference convention — no <c>display</c> field, since
    /// the Endpoint resource itself carries the operator-authored name
    /// and consumers fetch it via the bundled Reference. Decision 8
    /// ordering applied.
    /// </para>
    /// </summary>
    private JsonArray BuildEndpointReferences(BenefitPlan plan)
    {
        var array = new JsonArray();
        foreach (var doc in _endpointProjector.OrderedProjectableDocuments(plan))
        {
            array.Add(new JsonObject
            {
                ["reference"] = $"Endpoint/{doc.Id}",
            });
        }
        return array;
    }

    // ── network references ──────────────────────────────────────────────

    /// <summary>
    /// Build top-level <c>InsurancePlan.network[]</c> emitting ALL
    /// non-null-NetworkId tiers, ordered by TierLevel ascending
    /// (Decision 10 revision — prompt's "first tier only" was wrong
    /// because <c>network</c> cardinality is 0..*).
    /// </summary>
    private static JsonArray BuildNetworkReferences(
        IEnumerable<NetworkTier> tiers,
        IReadOnlyList<OrganizationLookupResult>? lookup)
    {
        var array = new JsonArray();
        if (tiers is null) return array;

        var ordered = tiers
            .Where(t => !string.IsNullOrWhiteSpace(t.NetworkId))
            .OrderBy(t => t.TierLevel)
            .ThenBy(t => t.TierName ?? string.Empty);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tier in ordered)
        {
            var networkId = tier.NetworkId!;
            if (!seen.Add(networkId)) continue;

            array.Add(BuildOrganizationReference(networkId, lookup));
        }

        return array;
    }

    private static JsonObject BuildOrganizationReference(
        string networkId,
        IReadOnlyList<OrganizationLookupResult>? lookup)
    {
        var reference = new JsonObject
        {
            ["reference"] = $"Organization/{networkId}",
        };

        if (lookup is not null)
        {
            var match = lookup.FirstOrDefault(o =>
                string.Equals(o.OrganizationId, networkId, StringComparison.Ordinal));
            if (match is not null && !string.IsNullOrEmpty(match.Name))
            {
                reference["display"] = match.Name;
            }
        }

        return reference;
    }

    // ── coverage[] ──────────────────────────────────────────────────────

    /// <summary>
    /// One <c>coverage</c> entry per benefit-type group (medical /
    /// pharmacy / dental / vision / behavioralHealth / dme / maternity /
    /// preventive). Each entry repeats the top-level network references
    /// and emits per-Benefit detail under <c>benefit[]</c>.
    /// </summary>
    private static JsonArray BuildCoverage(BenefitPlan plan, JsonArray topLevelNetworks)
    {
        var coverage = new JsonArray();
        if (plan.Benefits is null || plan.Benefits.Count == 0) return coverage;

        var grouped = plan.Benefits
            .Where(b => b is not null)
            .GroupBy(b => b.BenefitType ?? BenefitTypeDiscriminators.Medical)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var entry = new JsonObject
            {
                ["type"] = CodeableConcept(coding: null, text: PrettyBenefitCategory(group.Key)),
            };

            // Plan-Net cardinality on coverage.network is 0..*. Repeat the
            // top-level set so each coverage block is self-contained.
            if (topLevelNetworks.Count > 0)
            {
                var networkClone = new JsonArray();
                foreach (var n in topLevelNetworks)
                {
                    networkClone.Add(CloneJsonObject(n));
                }
                entry["network"] = networkClone;
            }

            var benefitArray = new JsonArray();
            foreach (var benefit in group)
            {
                benefitArray.Add(BuildCoverageBenefit(benefit));
            }
            entry["benefit"] = benefitArray;

            coverage.Add(entry);
        }

        return coverage;
    }

    private static JsonObject BuildCoverageBenefit(Benefit benefit)
    {
        var node = new JsonObject
        {
            ["type"] = CodeableConcept(
                coding: null,
                text: !string.IsNullOrEmpty(benefit.ServiceCategory)
                    ? benefit.ServiceCategory
                    : benefit.Description),
        };

        // requirement — combine prior-auth flag and operator-authored
        // limitations into a single human-readable string. FHIR
        // cardinality is 0..1 string.
        var requirement = BuildRequirement(benefit);
        if (!string.IsNullOrEmpty(requirement))
        {
            node["requirement"] = requirement;
        }

        // limit[] — visit limits (Quantity) and dollar caps (Money).
        var limit = new JsonArray();
        if (benefit.VisitLimit.HasValue)
        {
            var unit = !string.IsNullOrEmpty(benefit.VisitLimitPeriod)
                ? benefit.VisitLimitPeriod!
                : "visit";
            limit.Add(new JsonObject
            {
                ["value"] = Quantity(benefit.VisitLimit.Value, unit),
            });
        }
        if (benefit.AnnualMaximum.HasValue)
        {
            limit.Add(new JsonObject
            {
                ["value"] = Money(benefit.AnnualMaximum.Value),
                ["code"] = CodeableConcept(coding: null, text: "Annual maximum"),
            });
        }
        if (benefit.LifetimeMaximum.HasValue)
        {
            limit.Add(new JsonObject
            {
                ["value"] = Money(benefit.LifetimeMaximum.Value),
                ["code"] = CodeableConcept(coding: null, text: "Lifetime maximum"),
            });
        }
        if (limit.Count > 0)
        {
            node["limit"] = limit;
        }

        return node;
    }

    private static string? BuildRequirement(Benefit benefit)
    {
        var parts = new List<string>(2);
        if (benefit.PriorAuthRequired || benefit.RequiresPriorAuth)
        {
            parts.Add("Prior authorization required.");
        }
        if (!string.IsNullOrWhiteSpace(benefit.Limitations))
        {
            parts.Add(benefit.Limitations!.Trim());
        }
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    // ── plan[] ──────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>plan</c> entry per NetworkTier with a non-null NetworkId.
    /// Each entry carries the tier's network reference, generalCost
    /// (deductible + OOP max), and specificCost (per-Benefit cost
    /// sharing scoped to this tier's in-network vs out-of-network
    /// columns on <see cref="CostSharing"/>).
    /// </summary>
    private static JsonArray BuildPlans(
        BenefitPlan plan,
        IReadOnlyList<OrganizationLookupResult>? networks,
        AcaLimits? acaLimits)
    {
        var planArray = new JsonArray();
        if (plan.NetworkTiers is null) return planArray;

        var tiers = plan.NetworkTiers
            .Where(t => !string.IsNullOrWhiteSpace(t.NetworkId))
            .OrderBy(t => t.TierLevel)
            .ThenBy(t => t.TierName ?? string.Empty)
            .ToList();

        var acaEnforced = AcaCapEnforcementPolicy.IsEnforced(plan);
        var primaryInNetworkSeen = false;

        foreach (var tier in tiers)
        {
            // The ACA per-member cap (Decision 11) projects on exactly
            // ONE generalCost block — the primary in-network tier
            // (lowest TierLevel among tiers we consider in-network).
            // After sorting by TierLevel ASC above, the first tier that
            // ResolveTierIsInNetwork classifies as in-network is the
            // primary; subsequent in-network tiers (Tier 2 Preferred,
            // etc.) must NOT duplicate the entry.
            var isPrimaryInNetwork = !primaryInNetworkSeen && ResolveTierIsInNetwork(tier);
            if (isPrimaryInNetwork) primaryInNetworkSeen = true;

            var entry = new JsonObject();

            // identifier — operator-authored TierName under CHO system.
            var tierIdentifierValue = !string.IsNullOrEmpty(tier.TierName)
                ? tier.TierName
                : (tier.NetworkId ?? string.Empty);
            entry["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = ChoBenefitPlanFhirUrls.NetworkTierSystem,
                    ["value"] = tierIdentifierValue,
                }
            };

            // type — text-only "Tier {TierLevel}".
            entry["type"] = CodeableConcept(coding: null, text: $"Tier {tier.TierLevel}");

            // network[] — single reference for this tier.
            entry["network"] = new JsonArray
            {
                BuildOrganizationReference(tier.NetworkId!, networks),
            };

            // generalCost[] — deductible + OOP max for this tier. Only
            // the primary in-network tier emits the ACA cap entry.
            var general = BuildGeneralCost(plan, tier, isPrimaryInNetwork && acaEnforced, acaLimits);
            if (general.Count > 0)
            {
                entry["generalCost"] = general;
            }

            // specificCost[] — per-Benefit cost-sharing for this tier.
            var specific = BuildSpecificCost(plan, tier);
            if (specific.Count > 0)
            {
                entry["specificCost"] = specific;
            }

            planArray.Add(entry);
        }

        return planArray;
    }

    /// <summary>
    /// Build <c>generalCost[]</c> for one tier. <paramref name="emitAcaCap"/>
    /// must be true for AT MOST ONE tier in the parent <c>plan[]</c> array
    /// (the primary in-network tier — see caller logic) so the per-member
    /// cap doesn't duplicate across Tier 1 + Tier 2 Preferred etc.
    /// </summary>
    private static JsonArray BuildGeneralCost(
        BenefitPlan plan,
        NetworkTier tier,
        bool emitAcaCap,
        AcaLimits? acaLimits)
    {
        var array = new JsonArray();
        var costs = plan.CostSharing ?? new CostSharing();
        var isInNetwork = ResolveTierIsInNetwork(tier);

        // Deductibles
        var indDeductible = isInNetwork
            ? PreferPositive(costs.IndividualDeductible, costs.InNetworkDeductible)
            : PreferPositive(
                costs.OutNetworkIndividualDeductible ?? 0m,
                costs.OutOfNetworkDeductible);
        var famDeductible = isInNetwork
            ? PreferPositive(costs.FamilyDeductible, 0m)
            : (costs.OutNetworkFamilyDeductible ?? 0m);

        if (indDeductible > 0)
            array.Add(BuildGeneralCostEntry("deductible", "Deductible", 1, indDeductible,
                $"Individual deductible ({(isInNetwork ? "in-network" : "out-of-network")})"));
        if (famDeductible > 0)
            array.Add(BuildGeneralCostEntry("deductible", "Deductible", 2, famDeductible,
                $"Family deductible ({(isInNetwork ? "in-network" : "out-of-network")})"));

        // OOP max
        var indOop = isInNetwork
            ? PreferPositive(costs.IndividualOutOfPocketMax, costs.InNetworkOutOfPocketMax)
            : PreferPositive(
                costs.OutNetworkIndividualOutOfPocketMax ?? 0m,
                costs.OutOfNetworkOutOfPocketMax);
        var famOop = isInNetwork
            ? PreferPositive(costs.FamilyOutOfPocketMax, 0m)
            : (costs.OutNetworkFamilyOutOfPocketMax ?? 0m);

        if (indOop > 0)
            array.Add(BuildGeneralCostEntry("out-of-pocket-max", "Out-of-Pocket Maximum", 1, indOop,
                $"Individual out-of-pocket maximum ({(isInNetwork ? "in-network" : "out-of-network")})"));
        if (famOop > 0)
            array.Add(BuildGeneralCostEntry("out-of-pocket-max", "Out-of-Pocket Maximum", 2, famOop,
                $"Family out-of-pocket maximum ({(isInNetwork ? "in-network" : "out-of-network")})"));

        // ACA per-member cap (Decision 11 dual emission). Caller must
        // restrict emitAcaCap to the primary in-network tier; the
        // isInNetwork check here is defense-in-depth in case a future
        // refactor flips that contract — out-of-network tiers must
        // never carry the ACA cap because OON OOP is independent of
        // ACA enforcement.
        if (emitAcaCap && isInNetwork && acaLimits is not null && acaLimits.IndividualCap > 0)
        {
            var entry = BuildGeneralCostEntry(
                "aca-individual-cap",
                "ACA Individual Cap",
                groupSize: 1,
                amount: acaLimits.IndividualCap,
                comment: $"ACA per-member out-of-pocket cap (45 CFR §156.130, plan year {acaLimits.PlanYear})");

            // Per-cost extension — disambiguate from a real plan-level
            // individual cap (Decision 11). Standard Plan-Net consumers
            // see the entry as a real cost limit; CHO-aware consumers
            // read the flag and know it's regulatory enforcement.
            entry["extension"] = new JsonArray
            {
                ExtensionBoolean(ChoBenefitPlanFhirUrls.AcaCapEnforcedExt, true),
            };
            array.Add(entry);
        }

        return array;
    }

    private static JsonObject BuildGeneralCostEntry(
        string code,
        string display,
        int groupSize,
        decimal amount,
        string comment)
    {
        var entry = new JsonObject
        {
            ["type"] = CodeableConcept(
                Coding(ChoBenefitPlanFhirUrls.PlanGeneralCostTypeSystem, code, display),
                text: display),
            ["groupSize"] = groupSize,
            ["cost"] = Money(amount),
            ["comment"] = comment,
        };
        return entry;
    }

    private static JsonArray BuildSpecificCost(BenefitPlan plan, NetworkTier tier)
    {
        var array = new JsonArray();
        if (plan.Benefits is null || plan.Benefits.Count == 0) return array;

        var isInNetwork = ResolveTierIsInNetwork(tier);

        var grouped = plan.Benefits
            .Where(b => b is not null)
            .GroupBy(b => b.BenefitType ?? BenefitTypeDiscriminators.Medical)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var benefitArray = new JsonArray();
            foreach (var benefit in group)
            {
                var costs = BuildBenefitCosts(benefit, isInNetwork);
                if (costs.Count == 0) continue;

                benefitArray.Add(new JsonObject
                {
                    ["type"] = CodeableConcept(
                        coding: null,
                        text: !string.IsNullOrEmpty(benefit.ServiceCategory)
                            ? benefit.ServiceCategory
                            : benefit.Description),
                    ["cost"] = costs,
                });
            }

            if (benefitArray.Count > 0)
            {
                array.Add(new JsonObject
                {
                    ["category"] = CodeableConcept(coding: null, text: PrettyBenefitCategory(group.Key)),
                    ["benefit"] = benefitArray,
                });
            }
        }

        return array;
    }

    private static JsonArray BuildBenefitCosts(Benefit benefit, bool isInNetwork)
    {
        var costs = new JsonArray();

        decimal? copay;
        decimal? coinsurance;
        if (isInNetwork)
        {
            copay = benefit.InNetworkCopay ?? benefit.CopayAmount;
            coinsurance = benefit.InNetworkCoinsurance ?? benefit.CoinsurancePercentage;
        }
        else
        {
            copay = benefit.OutNetworkCopay;
            coinsurance = benefit.OutNetworkCoinsurance;
        }

        var tierQualifier = isInNetwork ? QualifierInNetwork : QualifierOutOfNetwork;

        if (copay.HasValue && copay.Value >= 0)
        {
            costs.Add(BuildCostEntry(tierQualifier, QualifierCopay, Money(copay.Value)));
        }

        if (coinsurance.HasValue && coinsurance.Value >= 0)
        {
            // Coinsurance on Benefit is stored as a fraction (0.20 = 20%).
            // FHIR Quantity for percentage uses unit "%" with the UCUM
            // code "%" so consumers can render it cleanly.
            var pct = coinsurance.Value <= 1m ? coinsurance.Value * 100m : coinsurance.Value;
            costs.Add(BuildCostEntry(tierQualifier, QualifierCoinsurance,
                Quantity(pct, "%", system: "http://unitsofmeasure.org", code: "%")));
        }

        return costs;
    }

    private static JsonObject BuildCostEntry(string tierQualifier, string typeQualifier, JsonObject value)
    {
        // Plan-Net IG 1.1.0 expects qualifier codes; we publish them
        // under a CHO CodeSystem for stability (no canonical IG-published
        // CodeSystem exists for these slots). 2-element-array shape per
        // Decision 9. If conformance testing rejects the array shape,
        // fall back to one cost entry per single qualifier.
        return new JsonObject
        {
            ["qualifiers"] = new JsonArray
            {
                CodeableConcept(
                    Coding(ChoBenefitPlanFhirUrls.PlanCostQualifierSystem, tierQualifier, tierQualifier),
                    text: tierQualifier),
                CodeableConcept(
                    Coding(ChoBenefitPlanFhirUrls.PlanCostQualifierSystem, typeQualifier, typeQualifier),
                    text: typeQualifier),
            },
            ["value"] = value,
        };
    }

    // ── extensions ──────────────────────────────────────────────────────

    private static JsonArray BuildTopLevelExtensions(BenefitPlan plan)
    {
        var array = new JsonArray();

        array.Add(ExtensionCode(
            ChoBenefitPlanFhirUrls.FamilyAccumulatorModelExt,
            plan.FamilyAccumulatorModel.ToString()));

        if (AcaCapEnforcementPolicy.IsEnforced(plan))
        {
            array.Add(ExtensionBoolean(ChoBenefitPlanFhirUrls.AcaCapEnforcedExt, true));
        }

        return array;
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Tier is "out-of-network" only when its <c>TierName</c> carries an
    /// explicit out-of-network marker — the substring "out" + "network"
    /// (case-insensitive), or the prefix "OON". Anything else is treated
    /// as in-network and populates the IN columns on
    /// <see cref="CostSharing"/>.
    ///
    /// <para>
    /// <c>TierLevel</c> is intentionally NOT consulted: it's an
    /// operator-authored ordering hint, defaults to 0 on legacy plans,
    /// and conflating it with network classification would silently
    /// misclassify a Tier 0 OON or a Tier 2 in-network. Future tier-name
    /// shapes (e.g. "OOP-Preferred") that don't match either marker fall
    /// into in-network by default — surface as a future refinement if a
    /// concrete plan shape forces it.
    /// </para>
    /// </summary>
    private static bool ResolveTierIsInNetwork(NetworkTier tier)
    {
        var name = tier.TierName ?? string.Empty;
        if (name.Contains("out", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("network", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (name.StartsWith("OON", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static decimal PreferPositive(decimal first, decimal second)
        => first > 0 ? first : second;

    private static DateTime ResolveLastUpdated(BenefitPlan plan)
    {
        if (plan.ModifiedDate.HasValue) return plan.ModifiedDate.Value;
        if (plan.PublishedAt.HasValue) return plan.PublishedAt.Value;
        if (plan.UpdatedAt != default) return plan.UpdatedAt;
        return plan.CreatedAt;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string ToFhirDate(DateTime value)
        => ToUtc(value).ToString("yyyy-MM-dd");

    private static string ToFhirInstant(DateTime value)
        => ToUtc(value).ToString("o");

    private static string PrettyBenefitCategory(string discriminator) => discriminator switch
    {
        BenefitTypeDiscriminators.Medical => "Medical",
        BenefitTypeDiscriminators.Dental => "Dental",
        BenefitTypeDiscriminators.Pharmacy => "Pharmacy",
        BenefitTypeDiscriminators.Vision => "Vision",
        BenefitTypeDiscriminators.BehavioralHealth => "Behavioral Health",
        BenefitTypeDiscriminators.DME => "Durable Medical Equipment",
        BenefitTypeDiscriminators.Maternity => "Maternity",
        BenefitTypeDiscriminators.Preventive => "Preventive",
        _ => discriminator,
    };

    /// <summary>
    /// Deep-clone a JsonObject so the same Reference body can be repeated
    /// across multiple coverage[] entries without sharing node identity.
    /// JsonObject nodes are tree-attached; reusing one would crash the
    /// serializer with "node already has a parent."
    /// </summary>
    private static JsonObject CloneJsonObject(JsonNode? source)
    {
        return JsonNode.Parse(source?.ToJsonString() ?? "{}")?.AsObject()
            ?? new JsonObject();
    }
}
