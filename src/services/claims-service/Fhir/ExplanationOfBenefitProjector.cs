using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

public sealed class ExplanationOfBenefitProjector : IExplanationOfBenefitProjector
{
    public JsonObject Project(Claim claim)
    {
        var eob = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = claim.Id,
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

        if (claim.ClaimLines.Count > 0)
        {
            var items = new JsonArray();
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
                    ["category"] = SimpleCoding("submitted", "Submitted Amount"),
                    ["amount"] = Money(claim.TotalChargeAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleCoding("eligible", "Eligible Amount"),
                    ["amount"] = Money(adj.AllowedAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleCoding("benefit", "Benefit Amount"),
                    ["amount"] = Money(adj.PayerPayment)
                },
                new JsonObject
                {
                    ["category"] = SimpleCoding("copay", "Copay"),
                    ["amount"] = Money(adj.CopayAmount)
                },
                new JsonObject
                {
                    ["category"] = SimpleCoding("deductible", "Deductible"),
                    ["amount"] = Money(adj.DeductibleAmount)
                }
            };

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

    private static JsonObject SimpleCoding(string code, string display) => new()
    {
        ["coding"] = new JsonArray
        {
            new JsonObject
            {
                ["system"] = "http://terminology.hl7.org/CodeSystem/adjudication",
                ["code"] = code,
                ["display"] = display
            }
        }
    };
}
