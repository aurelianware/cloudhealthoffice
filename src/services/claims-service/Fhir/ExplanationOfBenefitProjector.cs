using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

public sealed class ExplanationOfBenefitProjector : IExplanationOfBenefitProjector
{
    // FHIR adjudication category system used for the standard total / line-level
    // categories (submitted, eligible, benefit, copay, deductible). The CARC /
    // adjudication-reason coding lives under a different system.
    private const string AdjudicationCategorySystem =
        "http://terminology.hl7.org/CodeSystem/adjudication";

    // X12 Claim Adjustment Reason Codes (CARC). Engine-suggested CARCs
    // (NCCI/MUE SuggestedCarc, AdjudicationResult.DenialReasonCode,
    // AdjustmentReasons.ReasonCode) are X12 codes — they live under the
    // X12 system URI, not the FHIR adjudication-reason valueset (which
    // is a different small codeset). CARIN BB consumers expect this
    // system URI on adjudication category coding.
    private const string CarcSystem =
        "https://x12.org/codes/claim-adjustment-reason-codes";

    // Generic CARC fallback when the engine surfaces an edit failure
    // without a SuggestedCarc — code 237 is "Legislated/Regulatory
    // Penalty," the closest generic adjudication signal.
    private const string DefaultEditFailureCarc = "237";

    // CHO-private system for advisory AI-examination disposition, surfaced as
    // FHIR supportingInfo so patient-access consumers can audit how a claim
    // was evaluated. The spec's CMS-0057-F transparency requirements are
    // satisfied by the disposition + ModelId + PromptVersion fields. Free-text
    // Rationale and PolicyCitations are deliberately omitted from Phase 1
    // (Decision 5) — they need a redaction/review gate before reaching
    // patient-facing surfaces.
    private const string AiExaminationDispositionSystem =
        "urn:cho:ai-examination-disposition";

    // CHO-private system for NCCI edit failure pair coding emitted on
    // item[].adjudication[].reason. Engines emit (Column1Code, Column2Code)
    // for pair edits; MUE failures use the rule id directly.
    private const string NcciEditSystem = "urn:cho:ncci-edit";

    public JsonObject Project(Claim claim)
    {
        // FHIR id is the chain-stable ClaimVersionId so consumers see one
        // resource id across adjustments. Hydrate already aliases legacy rows
        // (ClaimVersionId == Id) so this is a single read-path.
        var fhirId = string.IsNullOrEmpty(claim.ClaimVersionId)
            ? claim.Id
            : claim.ClaimVersionId;

        var eob = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = fhirId,
            // meta.lastUpdated drives FHIR _lastUpdated search and is part of
            // every Plan-Net / US-Core resource shape across BP 5.8 / Provider
            // 5.7-5.9 projectors. ISO 8601 with offset.
            ["meta"] = new JsonObject
            {
                ["lastUpdated"] = claim.LastUpdatedDate.ToString("o")
            },
            ["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = "urn:cho:claim-number",
                    ["value"] = claim.ClaimNumber
                }
            },
            ["status"] = MapStatus(claim.Status),
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = "http://terminology.hl7.org/CodeSystem/claim-type",
                        ["code"] = MapClaimType(claim.ClaimType)
                    }
                }
            },
            ["use"] = "claim",
            ["patient"] = new JsonObject
            {
                ["reference"] = $"Patient/{claim.MemberId}"
            },
            ["created"] = claim.SubmittedDate.ToString("o"),
            ["insurer"] = new JsonObject
            {
                ["display"] = "CloudHealthOffice"
            },
            ["provider"] = new JsonObject
            {
                ["identifier"] = new JsonObject
                {
                    ["system"] = "http://hl7.org/fhir/sid/us-npi",
                    ["value"] = claim.BillingProviderNPI
                },
                ["display"] = claim.BillingProviderName
            },
            ["outcome"] = MapOutcome(claim.Status),
            ["billablePeriod"] = new JsonObject
            {
                ["start"] = claim.ServiceDateFrom.ToString("yyyy-MM-dd"),
                ["end"] = claim.ServiceDateTo.ToString("yyyy-MM-dd")
            }
        };

        // insurance[] — Decision 15 (amended). Reference Coverage when the
        // claim carries CoverageId; omit the array entirely when null. Phase
        // 1 dereferences may 404 because coverage-service has no FHIR
        // Coverage projection yet — the structural reference remains valid
        // FHIR and forward-compats with that work.
        if (!string.IsNullOrEmpty(claim.CoverageId))
        {
            eob["insurance"] = new JsonArray
            {
                new JsonObject
                {
                    ["focal"] = true,
                    ["coverage"] = new JsonObject
                    {
                        ["reference"] = $"Coverage/{claim.CoverageId}"
                    }
                }
            };
        }

        if (claim.DiagnosisCodes.Count > 0)
        {
            var diagnoses = new JsonArray();
            foreach (var dx in claim.DiagnosisCodes)
            {
                diagnoses.Add(new JsonObject
                {
                    ["sequence"] = dx.PointerNumber,
                    ["diagnosisCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["system"] = "http://hl7.org/fhir/sid/icd-10-cm",
                                ["code"] = dx.Code,
                                ["display"] = dx.Description
                            }
                        }
                    }
                });
            }
            eob["diagnosis"] = diagnoses;
        }

        // supportingInfo[] aggregates header-level transparency entries. AI
        // examination disposition lands here per Decision 5/8 (claim-level
        // advisory; not line-level). 1-based sequence per the FHIR cardinality
        // rule.
        var supportingInfo = BuildSupportingInfo(claim);
        if (supportingInfo.Count > 0)
        {
            eob["supportingInfo"] = supportingInfo;
        }

        if (claim.ClaimLines.Count > 0)
        {
            var items = new JsonArray();
            // EditFailures bucketed by line number once so the per-line
            // adjudication mapping is O(lines + failures) instead of O(lines *
            // failures). Absent failures collection ⇒ empty bucket.
            var editFailuresByLine = BucketEditFailuresByLine(claim);

            foreach (var line in claim.ClaimLines)
            {
                var item = new JsonObject
                {
                    ["sequence"] = line.LineNumber,
                    ["productOrService"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["system"] = "http://www.ama-assn.org/go/cpt",
                                ["code"] = line.ProcedureCode,
                                ["display"] = line.ProcedureDescription
                            }
                        }
                    },
                    ["servicedPeriod"] = new JsonObject
                    {
                        ["start"] = line.ServiceDateFrom.ToString("yyyy-MM-dd"),
                        ["end"] = line.ServiceDateTo.ToString("yyyy-MM-dd")
                    },
                    ["quantity"] = new JsonObject
                    {
                        ["value"] = (decimal)line.Units
                    },
                    ["unitPrice"] = Money(line.ChargeAmount),
                    ["net"] = Money(line.ChargeAmount * line.Units)
                };

                // item[].adjudication[] — Decision 9. Each NCCI/MUE failure
                // affecting this line emits one adjudication entry whose
                // category coding is the engine-suggested CARC (or fallback
                // 237) and whose reason coding identifies the engine rule.
                if (editFailuresByLine.TryGetValue(line.LineNumber, out var failures))
                {
                    item["adjudication"] = BuildLineAdjudicationsFromEditFailures(failures);
                }

                items.Add(item);
            }
            eob["item"] = items;
        }

        if (claim.AdjudicationResult is { } adj)
        {
            eob["total"] = new JsonArray
            {
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("submitted", "Submitted Amount"),
                    ["amount"] = Money(claim.TotalChargeAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("eligible", "Eligible Amount"),
                    ["amount"] = Money(adj.AllowedAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("benefit", "Benefit Amount"),
                    ["amount"] = Money(adj.PayerPayment)
                },
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("copay", "Copay"),
                    ["amount"] = Money(adj.CopayAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("deductible", "Deductible"),
                    ["amount"] = Money(adj.DeductibleAmount)
                },
                // 5.11 — Decision 4 totals expansion. Coinsurance and
                // patient-responsibility complete the standard CARIN BB
                // total set; consumers expect both alongside the original
                // five categories.
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory("coinsurance", "Coinsurance"),
                    ["amount"] = Money(adj.CoinsuranceAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleAdjudicationCategory(
                        "patientresponsibility", "Patient Responsibility"),
                    ["amount"] = Money(adj.PatientResponsibility)
                }
            };

            // Header-level adjudication[] surfaces denial / adjustment context.
            // FHIR EOB allows adjudication on the resource header (not just
            // items) — patient-access apps and audit consumers read denial
            // explanations from the header so they don't need to reconstruct
            // them from line-level adjudication entries.
            var headerAdjudications = BuildHeaderAdjudications(adj);
            if (headerAdjudications.Count > 0)
            {
                eob["adjudication"] = headerAdjudications;
            }

            if (adj.PaymentDate is { } paymentDate)
            {
                eob["payment"] = new JsonObject
                {
                    ["date"] = paymentDate.ToString("yyyy-MM-dd"),
                    ["amount"] = Money(adj.PayerPayment)
                };
            }
        }

        return eob;
    }

    // ── supportingInfo[] ──────────────────────────────────────────────────────

    private static JsonArray BuildSupportingInfo(Claim claim)
    {
        var supportingInfo = new JsonArray();

        if (claim.AiExamination is { } ai)
        {
            // Decision 5 — emit disposition + confidence + model/prompt
            // attribution for CMS-0057-F audit trail. Free-text Rationale
            // and PolicyCitations are deferred until a redaction gate
            // exists; this keeps unredacted LLM output off patient-facing
            // surfaces.
            var entry = new JsonObject
            {
                ["sequence"] = supportingInfo.Count + 1,
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://terminology.hl7.org/CodeSystem/claiminformationcategory",
                            ["code"] = "info"
                        }
                    }
                },
                ["code"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = AiExaminationDispositionSystem,
                            ["code"] = ai.RecommendedDisposition,
                            ["display"] = ai.RecommendedDisposition
                        }
                    }
                },
                // ConfidenceScore is a 0.0–1.0 self-reported value; the
                // FHIR convention for arbitrary scalar attribution is
                // valueString rather than a typed valueDecimal so apps
                // that only inspect strings still see it.
                ["valueString"] =
                    $"Confidence: {ai.ConfidenceScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}"
            };

            if (!string.IsNullOrEmpty(ai.ModelId) || !string.IsNullOrEmpty(ai.PromptVersion))
            {
                // Concatenate model + prompt attribution into a second
                // valueString-equivalent field via reason. Patient apps
                // typically ignore reason; auditors read it.
                var attribution = new List<string>(2);
                if (!string.IsNullOrEmpty(ai.ModelId))
                    attribution.Add($"model={ai.ModelId}");
                if (!string.IsNullOrEmpty(ai.PromptVersion))
                    attribution.Add($"prompt={ai.PromptVersion}");

                entry["reason"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = AiExaminationDispositionSystem,
                            ["code"] = "attribution",
                            ["display"] = string.Join(" ", attribution)
                        }
                    }
                };
            }

            entry["timingDateTime"] = ai.GeneratedAt.ToString("o");
            supportingInfo.Add(entry);
        }

        return supportingInfo;
    }

    // ── item[].adjudication[] for NCCI/MUE edit failures ────────────────────

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

    private static JsonArray BuildLineAdjudicationsFromEditFailures(
        List<NcciEditFailureSnapshot> failures)
    {
        var adjudications = new JsonArray();
        foreach (var failure in failures)
        {
            var carc = string.IsNullOrEmpty(failure.SuggestedCarc)
                ? DefaultEditFailureCarc
                : failure.SuggestedCarc!;

            var entry = new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = CarcSystem,
                            ["code"] = carc
                        }
                    }
                },
                ["reason"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = NcciEditSystem,
                            ["code"] = BuildEditReasonCode(failure),
                            ["display"] = failure.Message ?? failure.RuleId
                        }
                    }
                }
            };

            if (!string.IsNullOrEmpty(failure.SuggestedRarc))
            {
                entry["extension"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["url"] = "urn:cho:ncci-rarc",
                        ["valueString"] = failure.SuggestedRarc
                    }
                };
            }

            adjudications.Add(entry);
        }
        return adjudications;
    }

    /// <summary>
    /// Build the engine-rule coding code for an NCCI/MUE failure. Pair
    /// edits encode "{Column1}-{Column2}"; MUE / non-pair edits fall back
    /// to the rule id so the coding is never empty.
    /// </summary>
    private static string BuildEditReasonCode(NcciEditFailureSnapshot failure)
    {
        if (!string.IsNullOrEmpty(failure.Column1Code) &&
            !string.IsNullOrEmpty(failure.Column2Code))
        {
            return $"{failure.Column1Code}-{failure.Column2Code}";
        }
        return string.IsNullOrEmpty(failure.RuleId) ? "edit" : failure.RuleId;
    }

    // ── Header adjudication[] for denial / adjustment context ──────────────

    private static JsonArray BuildHeaderAdjudications(AdjudicationResult adj)
    {
        var entries = new JsonArray();

        // Denial CARC + free-text description. The denial reason is what
        // CARIN BB / CMS-0057-F consumers look for first — surface it as
        // the leading adjudication entry whenever a denial code is present.
        if (!string.IsNullOrEmpty(adj.DenialReasonCode))
        {
            var denial = new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = CarcSystem,
                            ["code"] = adj.DenialReasonCode!
                        }
                    }
                }
            };
            if (!string.IsNullOrEmpty(adj.DenialReason))
            {
                denial["reason"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = CarcSystem,
                            ["code"] = adj.DenialReasonCode!,
                            ["display"] = adj.DenialReason
                        }
                    }
                };
            }
            entries.Add(denial);
        }

        // CARC adjustment reasons (CO/PR/PI/OA group + numeric reason code).
        // 835 callers depend on these to reconstruct the adjustment trail;
        // surfacing them on the FHIR header lets patient-access apps render
        // the same explanation without needing to walk line-level entries.
        foreach (var reason in adj.AdjustmentReasons)
        {
            entries.Add(new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = CarcSystem,
                            ["code"] = reason.ReasonCode,
                            ["display"] = reason.Description
                        }
                    },
                    ["text"] = reason.GroupCode
                },
                ["amount"] = Money(reason.Amount)
            });
        }

        // RARC remark codes — FHIR pattern is one adjudication entry per
        // remark with category coding under the RARC system. The remark
        // system URI is the HL7 X12-claim-payment-remark slot.
        foreach (var remark in adj.RemarkCodes)
        {
            entries.Add(new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "https://x12.org/codes/remittance-advice-remark-codes",
                            ["code"] = remark
                        }
                    }
                }
            });
        }

        return entries;
    }

    // ── Status / type / outcome mappers ───────────────────────────────────────

    // FHIR EOB.status: active | cancelled | draft | entered-in-error
    private static string MapStatus(ClaimStatus status) => status switch
    {
        ClaimStatus.Voided => "cancelled",
        ClaimStatus.Submitted or ClaimStatus.Received or ClaimStatus.InAdjudication
            or ClaimStatus.Pended => "draft",
        _ => "active"
    };

    // Common FHIR claim-type codes (http://terminology.hl7.org/CodeSystem/claim-type)
    private static string MapClaimType(ClaimType type) => type switch
    {
        ClaimType.Professional => "professional",
        ClaimType.Institutional => "institutional",
        ClaimType.Dental => "oral",
        _ => "professional"
    };

    // FHIR EOB.outcome: queued | complete | error | partial
    private static string MapOutcome(ClaimStatus status) => status switch
    {
        ClaimStatus.Paid or ClaimStatus.Approved => "complete",
        ClaimStatus.PartiallyPaid => "partial",
        ClaimStatus.Denied => "error",
        _ => "queued"
    };

    private static JsonObject Money(decimal amount) => new()
    {
        ["value"] = amount,
        ["currency"] = "USD"
    };

    private static JsonObject SimpleAdjudicationCategory(string code, string display) => new()
    {
        ["coding"] = new JsonArray
        {
            new JsonObject
            {
                ["system"] = AdjudicationCategorySystem,
                ["code"] = code,
                ["display"] = display
            }
        }
    };
}
