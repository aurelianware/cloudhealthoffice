using System.Text.Json.Nodes;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Money;

namespace ClaimsService.Fhir;

/// <summary>
/// The CHO claim record as a CARIN Blue Button <b>financial</b>
/// <c>ExplanationOfBenefit</c> — the payload CMS-0057-F's Patient Access API
/// carries, and what a member's own app reads.
///
/// <para>
/// This is the other half of the pair <see cref="CarinNonFinancialEobProjector"/>
/// opens. CARIN BB names each financial profile and its non-financial
/// counterpart identically but for a <c>-Basis</c> suffix, and the Basis
/// profile is the financial one's <c>baseDefinition</c>: the financial profile
/// requires everything the Basis profile does, and adds the plan's money. So
/// the shaping is <see cref="CarinEobBasis"/>'s, shared, and this projector's
/// own work is the money and the reasons:
/// </para>
///
/// <list type="bullet">
///   <item><description><c>item.net</c> and <c>item.unitPrice</c>, which the
///     Basis profiles forbid.</description></item>
///   <item><description>An <c>adjudicationamounttype</c> slice per item, which
///     the professional and oral profiles require and which is what takes
///     <c>item.adjudication</c> to the two entries they ask
///     for.</description></item>
///   <item><description><c>total</c>, which every financial profile requires at
///     least one of, with a category from C4BB's own adjudication value
///     set.</description></item>
///   <item><description>The denial, adjustment and remark trail, as
///     <c>adjustmentreason</c> slices.</description></item>
/// </list>
///
/// <para>
/// Why the reasons are sliced rather than left where they were: until this
/// projection declared a profile, each CARC was emitted as the adjudication
/// <c>category</c>. CARIN BB binds <c>category</c> to its own discriminator
/// value set and puts the X12 code in <c>reason</c> under the
/// <c>adjustmentreason</c> slice, so the old shape named a CARC where a
/// consumer looks for a discriminator. The codes are unchanged; where they sit
/// is.
/// </para>
///
/// <para>
/// Known conformance limits, checked by the HL7 validator in CI (see
/// <c>scripts/interop/fhir-validate.sh</c>) and recorded in
/// <c>conformance/validator-baseline.txt</c> rather than papered over: the
/// limits <see cref="CarinNonFinancialEobProjector"/> documents apply here too,
/// since the shaping is the same. On top of them, the AI-examination entry
/// carries a <c>claiminformationcategory</c> category because C4BB's own
/// supporting-info value set has no concept for an advisory model disposition;
/// the binding is extensible, which is what permits it.
/// </para>
/// </summary>
public sealed class ExplanationOfBenefitProjector : IExplanationOfBenefitProjector
{
    // FHIR adjudication categories. Five of them — submitted, eligible,
    // benefit, copay, deductible — are members of C4BB's own C4BBAdjudication
    // value set, which is why the totals below can keep using them under a
    // required binding.
    private const string AdjudicationCategorySystem =
        "http://terminology.hl7.org/CodeSystem/adjudication";

    // The rest of the total set lives in C4BB's code system rather than the
    // FHIR one: coinsurance and member liability have no FHIR adjudication
    // code, and "patientresponsibility" — which this projection used to emit —
    // is not a code in either system.
    private const string C4BbAdjudicationSystem =
        "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBAdjudication";

    // X12 Claim Adjustment Reason Codes (CARC). Engine-suggested CARCs
    // (NCCI/MUE SuggestedCarc, AdjudicationResult.DenialReasonCode,
    // AdjustmentReasons.ReasonCode) are X12 codes, and CARIN BB binds
    // adjudication.reason on the adjustmentreason slice to a value set that
    // draws from this system.
    private const string CarcSystem =
        "https://x12.org/codes/claim-adjustment-reason-codes";

    // X12 Remittance Advice Remark Codes (RARC). The same bound value set
    // covers these, so a remark is an adjustment reason too.
    private const string RarcSystem =
        "https://x12.org/codes/remittance-advice-remark-codes";

    // Generic CARC fallback when the engine surfaces an edit failure
    // without a SuggestedCarc — code 237 is "Legislated/Regulatory
    // Penalty," the closest generic adjudication signal.
    private const string DefaultEditFailureCarc = "237";

    // CARC 18 — exact duplicate claim/service; fallback when a finding
    // carries no suggested CARC.
    private const string DefaultDuplicateCarc = "18";

    // CHO-private system for advisory AI-examination disposition, surfaced as
    // FHIR supportingInfo so patient-access consumers can audit how a claim
    // was evaluated. The spec's CMS-0057-F transparency requirements are
    // satisfied by the disposition + ModelId + PromptVersion fields. Free-text
    // Rationale and PolicyCitations are deliberately omitted from Phase 1
    // (Decision 5) — they need a redaction/review gate before reaching
    // patient-facing surfaces.
    private const string AiExaminationDispositionSystem =
        "urn:cho:ai-examination-disposition";

    // CHO-private system for NCCI edit failure pair coding, emitted as a second
    // coding beside the CARC on item[].adjudication[].reason. Engines emit
    // (Column1Code, Column2Code) for pair edits; MUE failures use the rule id.
    private const string NcciEditSystem = "urn:cho:ncci-edit";

    // CHO-private system for duplicate-claim findings (DuplicateClaimStage),
    // emitted the same way. Codes are the stage rule ids: DUP001 (exact
    // duplicate) / DUP002 (suspect duplicate).
    private const string DuplicateEditSystem = "urn:cho:duplicate-edit";

    public JsonObject Project(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // EditFailures and duplicate findings bucketed by line once so the
        // per-line mapping is O(lines + failures) rather than O(lines *
        // failures).
        var editFailuresByLine = BucketEditFailuresByLine(claim);
        var duplicateFindingsByLine = BucketDuplicateFindingsByLine(claim);

        var eob = CarinEobBasis.Scaffold(
            claim,
            CarinEobBasis.Shape(claim, financial: true),
            augmentItem: (item, line) =>
                AddLineMoney(item, line, editFailuresByLine, duplicateFindingsByLine),
            augmentSupportingInfo: entries => AddAiExamination(claim, entries),
            augmentHeaderAdjudication: entries => AddAdjustmentReasons(claim, entries));

        // total is 1..* on every financial profile, so it is emitted whether or
        // not the claim has been adjudicated: what was submitted is known from
        // the moment the claim arrives, and an unadjudicated claim saying
        // nothing about money at all would not be a conformant instance.
        eob["total"] = Totals(claim);

        if (claim.AdjudicationResult?.PaymentDate is { } paymentDate)
        {
            eob["payment"] = new JsonObject
            {
                ["date"] = paymentDate.ToString("yyyy-MM-dd"),
                ["amount"] = Money(claim.AdjudicationResult.PayerPayment),
            };
        }

        return eob;
    }

    // ── item money ───────────────────────────────────────────────────────────

    /// <summary>
    /// The money on one line: what was charged, what it works out to per unit,
    /// and the adjudication entries the financial profiles require.
    /// </summary>
    private static void AddLineMoney(
        JsonObject item,
        ClaimLine line,
        Dictionary<int, List<NcciEditFailureSnapshot>> editFailuresByLine,
        Dictionary<int, List<DuplicateFindingSnapshot>> duplicateFindingsByLine)
    {
        // ClaimLine.ChargeAmount is the line TOTAL (837 SV102/SV203), which is
        // exactly FHIR item.net.
        item["net"] = Money(line.ChargeAmount);

        // unitPrice is derived (net / quantity). Omitted when the quantity is
        // zero rather than dividing by zero.
        if (line.Units > 0)
        {
            item["unitPrice"] = Money(MoneyRounding.ToCents(line.ChargeAmount / line.Units));
        }

        var adjudication = item["adjudication"] as JsonArray;
        if (adjudication is null)
        {
            adjudication = new JsonArray();
            item["adjudication"] = adjudication;
        }

        // The adjudicationamounttype slice. The line's charge is the one amount
        // CHO holds per line — allowed, paid, copay and deductible are
        // adjudicated at the claim header — so it is the one amount emitted
        // here, and it is what takes the professional and oral profiles'
        // item.adjudication to the two entries they require, beside the benefit
        // payment status the Basis shaping already added.
        adjudication.Add(new JsonObject
        {
            ["category"] = Category(AdjudicationCategorySystem, "submitted", "Submitted Amount"),
            ["amount"] = Money(line.ChargeAmount),
        });

        if (editFailuresByLine.TryGetValue(line.LineNumber, out var failures))
        {
            foreach (var entry in EditFailureAdjustments(failures))
                adjudication.Add(entry);
        }

        // Duplicate-claim findings are coded under their own system
        // (urn:cho:duplicate-edit), never as NCCI edits.
        if (duplicateFindingsByLine.TryGetValue(line.LineNumber, out var duplicates))
        {
            foreach (var entry in DuplicateAdjustments(duplicates))
                adjudication.Add(entry);
        }
    }

    // ── totals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The total set. Every category is a member of C4BB's own adjudication
    /// value set, which the financial profiles bind <c>total.category</c> to as
    /// <b>required</b> on the <c>adjudicationamounttype</c> slice — so a
    /// category outside it is not a looser description, it is a rejected
    /// instance.
    /// </summary>
    private static JsonArray Totals(Claim claim)
    {
        var totals = new JsonArray
        {
            new JsonObject
            {
                ["category"] = Category(AdjudicationCategorySystem, "submitted", "Submitted Amount"),
                ["amount"] = Money(claim.TotalChargeAmount),
            },
        };

        if (claim.AdjudicationResult is not { } adj)
            return totals;

        totals.Add(new JsonObject
        {
            ["category"] = Category(AdjudicationCategorySystem, "eligible", "Eligible Amount"),
            ["amount"] = Money(adj.AllowedAmount),
        });
        totals.Add(new JsonObject
        {
            ["category"] = Category(AdjudicationCategorySystem, "benefit", "Benefit Amount"),
            ["amount"] = Money(adj.PayerPayment),
        });
        totals.Add(new JsonObject
        {
            ["category"] = Category(AdjudicationCategorySystem, "copay", "Copay"),
            ["amount"] = Money(adj.CopayAmount),
        });
        totals.Add(new JsonObject
        {
            ["category"] = Category(AdjudicationCategorySystem, "deductible", "Deductible"),
            ["amount"] = Money(adj.DeductibleAmount),
        });

        // Coinsurance and member liability are C4BB's own codes. The FHIR
        // adjudication system has neither, and the code this projection used to
        // emit for the second — "patientresponsibility" — is not defined in any
        // system, so it was never a code a consumer could resolve.
        totals.Add(new JsonObject
        {
            ["category"] = Category(C4BbAdjudicationSystem, "coinsurance", "Co-insurance"),
            ["amount"] = Money(adj.CoinsuranceAmount),
        });
        totals.Add(new JsonObject
        {
            ["category"] = Category(C4BbAdjudicationSystem, "memberliability", "Member liability"),
            ["amount"] = Money(adj.PatientResponsibility),
        });

        return totals;
    }

    // ── supportingInfo ───────────────────────────────────────────────────────

    private static void AddAiExamination(Claim claim, JsonArray supportingInfo)
    {
        if (claim.AiExamination is not { } ai)
            return;

        // Decision 5 — emit disposition + confidence + model/prompt attribution
        // for the CMS-0057-F audit trail. Free-text Rationale and
        // PolicyCitations are deferred until a redaction gate exists; this keeps
        // unredacted model output off patient-facing surfaces.
        var entry = new JsonObject
        {
            // Continues the Basis shaping's own numbering: FHIR requires the
            // sequence to be unique within the resource, and the entries the
            // profile asks for are already numbered from 1.
            ["sequence"] = supportingInfo.Count + 1,
            ["category"] = Category(
                "http://terminology.hl7.org/CodeSystem/claiminformationcategory", "info", null),
            ["code"] = Category(AiExaminationDispositionSystem,
                ai.RecommendedDisposition, ai.RecommendedDisposition),
            // ConfidenceScore is a 0.0–1.0 self-reported value; the FHIR
            // convention for arbitrary scalar attribution is valueString rather
            // than a typed valueDecimal so apps that only inspect strings still
            // see it.
            ["valueString"] =
                $"Confidence: {ai.ConfidenceScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}",
        };

        if (!string.IsNullOrEmpty(ai.ModelId) || !string.IsNullOrEmpty(ai.PromptVersion))
        {
            // Model and prompt attribution. Patient apps typically ignore
            // reason; auditors read it.
            var attribution = new List<string>(2);
            if (!string.IsNullOrEmpty(ai.ModelId))
                attribution.Add($"model={ai.ModelId}");
            if (!string.IsNullOrEmpty(ai.PromptVersion))
                attribution.Add($"prompt={ai.PromptVersion}");

            entry["reason"] = Category(
                AiExaminationDispositionSystem, "attribution", string.Join(" ", attribution));
        }

        entry["timingDateTime"] = ai.GeneratedAt.ToString("o");
        supportingInfo.Add(entry);
    }

    // ── header adjudication: the adjustment trail ────────────────────────────

    /// <summary>
    /// The denial, adjustment and remark trail, appended to the network-status
    /// entries the Basis shaping emits.
    ///
    /// <para>
    /// Every entry is the <c>adjustmentreason</c> slice: category names the
    /// discriminator and <c>reason</c> carries the X12 code. CARIN BB binds
    /// that reason to a value set drawn from both the CARC and the RARC code
    /// systems, so a remark belongs in the same slice as an adjustment rather
    /// than in a category of its own.
    /// </para>
    /// </summary>
    private static void AddAdjustmentReasons(Claim claim, JsonArray entries)
    {
        if (claim.AdjudicationResult is not { } adj)
            return;

        if (!string.IsNullOrEmpty(adj.DenialReasonCode))
        {
            entries.Add(AdjustmentReason(
                CarcSystem, adj.DenialReasonCode!, adj.DenialReason, amount: null));
        }

        // CARC adjustment reasons (CO/PR/PI/OA group + numeric reason code).
        // 835 callers depend on these to reconstruct the adjustment trail.
        foreach (var reason in adj.AdjustmentReasons)
        {
            var entry = AdjustmentReason(
                CarcSystem, reason.ReasonCode, reason.Description, Money(reason.Amount));

            // The X12 group code (CO/PR/PI/OA) says who the adjustment falls
            // to. It has no element of its own here, and it is not a CARC, so it
            // stays the reason's text rather than becoming a coding that would
            // fail the bound value set.
            if (!string.IsNullOrWhiteSpace(reason.GroupCode))
                entry["reason"]!.AsObject()["text"] = reason.GroupCode;

            entries.Add(entry);
        }

        foreach (var remark in adj.RemarkCodes)
            entries.Add(AdjustmentReason(RarcSystem, remark, null, amount: null));
    }

    private static JsonObject AdjustmentReason(
        string system, string? code, string? display, JsonObject? amount)
    {
        var entry = new JsonObject
        {
            ["category"] = Category(
                CarinEobBasis.AdjudicationDiscriminatorSystem, "adjustmentreason", null),
            ["reason"] = Category(system, code, display),
        };

        if (amount is not null)
            entry["amount"] = amount;

        return entry;
    }

    // ── item adjudication: engine findings ───────────────────────────────────

    private static List<JsonObject> EditFailureAdjustments(List<NcciEditFailureSnapshot> failures)
    {
        var adjudications = new List<JsonObject>(failures.Count);

        foreach (var failure in failures)
        {
            var carc = string.IsNullOrEmpty(failure.SuggestedCarc)
                ? DefaultEditFailureCarc
                : failure.SuggestedCarc!;

            // The CARC satisfies the bound value set; the engine rule rides
            // alongside it as a second coding in the same concept, so the
            // reason says both what the adjustment is and which rule produced
            // it.
            var entry = AdjustmentReason(CarcSystem, carc, null, amount: null);
            AddCoding(entry, NcciEditSystem, EditReasonCode(failure), failure.Message ?? failure.RuleId);

            if (!string.IsNullOrEmpty(failure.SuggestedRarc))
            {
                // A RARC qualifies the CARC rather than replacing it, and the
                // concept already carries two codings, so the remark is a third
                // under its own system.
                AddCoding(entry, RarcSystem, failure.SuggestedRarc, null);
            }

            adjudications.Add(entry);
        }

        return adjudications;
    }

    private static List<JsonObject> DuplicateAdjustments(List<DuplicateFindingSnapshot> findings)
    {
        var adjudications = new List<JsonObject>(findings.Count);

        foreach (var finding in findings)
        {
            var carc = string.IsNullOrEmpty(finding.SuggestedCarc)
                ? DefaultDuplicateCarc
                : finding.SuggestedCarc!;

            var entry = AdjustmentReason(CarcSystem, carc, null, amount: null);
            AddCoding(entry, DuplicateEditSystem, finding.RuleId,
                finding.Message ?? $"{finding.DuplicateType} duplicate");

            adjudications.Add(entry);
        }

        return adjudications;
    }

    /// <summary>
    /// Build the engine-rule coding code for an NCCI/MUE failure. Pair edits
    /// encode "{Column1}-{Column2}"; MUE / non-pair edits fall back to the rule
    /// id so the coding is never empty.
    /// </summary>
    private static string EditReasonCode(NcciEditFailureSnapshot failure)
    {
        if (!string.IsNullOrEmpty(failure.Column1Code) && !string.IsNullOrEmpty(failure.Column2Code))
            return $"{failure.Column1Code}-{failure.Column2Code}";

        return string.IsNullOrEmpty(failure.RuleId) ? "edit" : failure.RuleId;
    }

    // ── bucketing ────────────────────────────────────────────────────────────

    private static Dictionary<int, List<NcciEditFailureSnapshot>> BucketEditFailuresByLine(Claim claim)
    {
        var buckets = new Dictionary<int, List<NcciEditFailureSnapshot>>();
        if (claim.PendDetails is null || claim.PendDetails.EditFailures.Count == 0)
            return buckets;

        foreach (var failure in claim.PendDetails.EditFailures)
        {
            if (failure.AffectedLineNumbers.Count == 0) continue;
            foreach (var lineNumber in failure.AffectedLineNumbers)
            {
                if (!buckets.TryGetValue(lineNumber, out var list))
                {
                    list = new List<NcciEditFailureSnapshot>();
                    buckets[lineNumber] = list;
                }
                list.Add(failure);
            }
        }

        return buckets;
    }

    private static Dictionary<int, List<DuplicateFindingSnapshot>> BucketDuplicateFindingsByLine(Claim claim)
    {
        var buckets = new Dictionary<int, List<DuplicateFindingSnapshot>>();
        if (claim.PendDetails is null || claim.PendDetails.DuplicateFindings.Count == 0)
            return buckets;

        foreach (var finding in claim.PendDetails.DuplicateFindings)
        {
            if (!buckets.TryGetValue(finding.LineNumber, out var list))
            {
                list = new List<DuplicateFindingSnapshot>();
                buckets[finding.LineNumber] = list;
            }
            list.Add(finding);
        }

        return buckets;
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-coding concept whose <c>code</c> and <c>display</c> are present
    /// only when there is one. Assigning a null string through the
    /// <see cref="JsonObject"/> indexer writes a JSON <c>null</c>, and FHIR
    /// draws a line between an element that is absent and one that is present
    /// with no value: the HL7 validator rejects the second ("this property must
    /// be a simple value, not a Null"), which is how this came to exist.
    /// </summary>
    private static JsonObject Category(string system, string? code, string? display)
    {
        var coding = new JsonObject { ["system"] = system };
        if (!string.IsNullOrWhiteSpace(code))
            coding["code"] = code;
        if (!string.IsNullOrWhiteSpace(display))
            coding["display"] = display;

        return new JsonObject { ["coding"] = new JsonArray { coding } };
    }

    /// <summary>Adds a further coding to an adjustment reason's concept.</summary>
    private static void AddCoding(JsonObject adjustment, string system, string? code, string? display)
    {
        var coding = new JsonObject { ["system"] = system };
        if (!string.IsNullOrWhiteSpace(code))
            coding["code"] = code;
        if (!string.IsNullOrWhiteSpace(display))
            coding["display"] = display;

        adjustment["reason"]!.AsObject()["coding"]!.AsArray().Add(coding);
    }

    private static JsonObject Money(decimal amount) => new()
    {
        ["value"] = amount,
        ["currency"] = "USD",
    };
}
