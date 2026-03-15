using FhirService.Models;

namespace FhirService.Mappers;

/// <summary>
/// Maps CHO internal models to lightweight FHIR R4 resources for the Patient Access API.
/// Port of the TypeScript patient-access-mapper.ts and provider-access-api.ts mapping logic.
/// </summary>
public static class PatientAccessMapper
{
    private const string PayorDisplay = "Cloud Health Office Plan";

    // ── Member → Patient ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a CHO Member to a FHIR R4 Patient resource (US Core profile).
    /// </summary>
    public static FhirPatient MapMemberToPatient(ChoMember member)
    {
        var given = new List<string> { member.FirstName };
        if (!string.IsNullOrEmpty(member.MiddleName))
            given.Add(member.MiddleName);

        var patient = new FhirPatient
        {
            Id = member.MemberId,
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient"]
            },
            Identifier =
            [
                new FhirIdentifier
                {
                    Use = "official",
                    Type = new FhirCodeableConcept
                    {
                        Coding =
                        [
                            new FhirCoding
                            {
                                System = "http://terminology.hl7.org/CodeSystem/v2-0203",
                                Code = "MB",
                                Display = "Member Number"
                            }
                        ]
                    },
                    Value = member.MemberId
                }
            ],
            Active = true,
            Name =
            [
                new FhirHumanName
                {
                    Use = "official",
                    Family = member.LastName,
                    Given = given
                }
            ],
            Gender = MapGender(member.Gender),
            BirthDate = member.Dob
        };

        // Add address if available
        if (member.Address is { } addr)
        {
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(addr.Street1)) lines.Add(addr.Street1);
            if (!string.IsNullOrEmpty(addr.Street2)) lines.Add(addr.Street2);

            patient = patient with
            {
                Address =
                [
                    new FhirAddress
                    {
                        Use = "home",
                        Line = lines,
                        City = addr.City,
                        State = addr.State,
                        PostalCode = addr.Zip
                    }
                ]
            };
        }

        // Add telecom if available
        var telecom = new List<FhirContactPoint>();
        if (!string.IsNullOrEmpty(member.Phone))
        {
            telecom.Add(new FhirContactPoint { System = "phone", Value = member.Phone, Use = "home" });
        }
        if (!string.IsNullOrEmpty(member.Email))
        {
            telecom.Add(new FhirContactPoint { System = "email", Value = member.Email, Use = "home" });
        }
        if (telecom.Count > 0)
        {
            patient = patient with { Telecom = telecom };
        }

        return patient;
    }

    // ── Coverage → FHIR Coverage ────────────────────────────────────────────

    /// <summary>
    /// Maps a CHO Member to a FHIR R4 Coverage resource.
    /// </summary>
    public static FhirCoverage MapMemberToCoverage(ChoMember member)
    {
        return new FhirCoverage
        {
            Id = $"{member.MemberId}-COV",
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-coverage"]
            },
            Status = "active",
            Type = new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://terminology.hl7.org/CodeSystem/v3-ActCode",
                        Code = "SUBSCR"
                    }
                ]
            },
            Beneficiary = new FhirReference { Reference = $"Patient/{member.MemberId}" },
            SubscriberId = member.MemberId,
            Payor = [new FhirReference { Display = PayorDisplay }]
        };
    }

    // ── Claim → FHIR Claim (via BackendClaim) ──────────────────────────────

    /// <summary>
    /// Maps a CHO Claim to a FHIR R4 Claim resource.
    /// </summary>
    public static FhirResource MapClaimToFhirClaim(ChoClaim claim)
    {
        // Returns an anonymous-typed wrapper — but we use the base FhirResource type.
        // For the Patient Access API we only need the claim ID and resourceType for bundle entry.
        return new FhirClaimResource
        {
            Id = claim.ClaimId,
            Status = claim.Status,
            ClaimType = new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://terminology.hl7.org/CodeSystem/claim-type",
                        Code = claim.ClaimType.ToLowerInvariant(),
                        Display = claim.ClaimType
                    }
                ]
            },
            Use = "claim",
            Patient = new FhirReference { Reference = $"Patient/{claim.MemberId}" },
            Provider = new FhirReference { Reference = $"Practitioner/{claim.ProviderId}" },
            Created = DateTime.UtcNow.ToString("o"),
            Priority = new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://terminology.hl7.org/CodeSystem/processpriority",
                        Code = "normal"
                    }
                ]
            },
            Insurance =
            [
                new FhirEobInsurance
                {
                    Focal = true,
                    Coverage = new FhirReference { Reference = $"Coverage/{claim.MemberId}" }
                }
            ]
        };
    }

    // ── Payment → ExplanationOfBenefit ──────────────────────────────────────

    /// <summary>
    /// Maps a CHO PaymentDocument to a FHIR R4 ExplanationOfBenefit resource.
    /// </summary>
    public static FhirExplanationOfBenefit MapPaymentToEob(ChoPaymentDocument payment)
    {
        return new FhirExplanationOfBenefit
        {
            Id = payment.PaymentId,
            Status = payment.Status ?? "active",
            Type = new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://terminology.hl7.org/CodeSystem/claim-type",
                        Code = "professional"
                    }
                ]
            },
            Use = "claim",
            Patient = new FhirReference { Reference = $"Patient/{payment.MemberId}" },
            Created = payment.PaymentDate,
            Insurer = new FhirReference { Display = PayorDisplay },
            Provider = new FhirReference { Display = "Rendering Provider" },
            Outcome = "complete",
            Insurance =
            [
                new FhirEobInsurance
                {
                    Focal = true,
                    Coverage = new FhirReference { Reference = $"Coverage/{payment.MemberId}-COV" }
                }
            ],
            Payment = new FhirEobPayment
            {
                Amount = new FhirMoney { Value = payment.TotalPaid, Currency = "USD" }
            },
            SupportingInfo =
            [
                new FhirEobSupportingInfo
                {
                    Sequence = 1,
                    Category = new FhirCodeableConcept
                    {
                        Coding =
                        [
                            new FhirCoding
                            {
                                System = "http://terminology.hl7.org/CodeSystem/eob-information",
                                Code = "clmrecv"
                            }
                        ]
                    },
                    ValueString = $"Claim {payment.ClaimId}"
                }
            ]
        };
    }

    // ── Bundle builders ─────────────────────────────────────────────────────

    /// <summary>
    /// Wraps Patient resources in a FHIR searchset Bundle.
    /// </summary>
    public static FhirBundle PatientsToBundle(IReadOnlyList<ChoMember> members, string selfLink)
    {
        var entries = members.Select(m =>
        {
            var patient = MapMemberToPatient(m);
            return new FhirBundleEntry
            {
                FullUrl = $"Patient/{patient.Id}",
                Resource = patient
            };
        }).ToList();

        return BuildBundle(entries, selfLink);
    }

    /// <summary>
    /// Wraps Coverage resources in a FHIR searchset Bundle.
    /// </summary>
    public static FhirBundle CoverageToBundle(IReadOnlyList<ChoMember> members, string selfLink)
    {
        var entries = members.Select(m =>
        {
            var coverage = MapMemberToCoverage(m);
            return new FhirBundleEntry
            {
                FullUrl = $"Coverage/{coverage.Id}",
                Resource = coverage
            };
        }).ToList();

        return BuildBundle(entries, selfLink);
    }

    /// <summary>
    /// Wraps Claim resources in a FHIR searchset Bundle.
    /// </summary>
    public static FhirBundle ClaimsToBundle(IReadOnlyList<ChoClaim> claims, string selfLink)
    {
        var entries = claims.Select(c =>
        {
            var fhirClaim = MapClaimToFhirClaim(c);
            return new FhirBundleEntry
            {
                FullUrl = $"Claim/{fhirClaim.Id}",
                Resource = fhirClaim
            };
        }).ToList();

        return BuildBundle(entries, selfLink);
    }

    /// <summary>
    /// Wraps ExplanationOfBenefit resources in a FHIR searchset Bundle.
    /// </summary>
    public static FhirBundle PaymentsToEobBundle(IReadOnlyList<ChoPaymentDocument> payments, string selfLink)
    {
        var entries = payments.Select(p =>
        {
            var eob = MapPaymentToEob(p);
            return new FhirBundleEntry
            {
                FullUrl = $"ExplanationOfBenefit/{eob.Id}",
                Resource = eob
            };
        }).ToList();

        return BuildBundle(entries, selfLink);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static FhirBundle BuildBundle(List<FhirBundleEntry> entries, string selfLink)
    {
        return new FhirBundle
        {
            Total = entries.Count,
            Link = [new FhirBundleLink { Relation = "self", Url = selfLink }],
            Entry = entries
        };
    }

    private static string MapGender(string gender)
    {
        return gender.ToUpperInvariant() switch
        {
            "M" or "MALE" => "male",
            "F" or "FEMALE" => "female",
            "O" or "OTHER" => "other",
            _ => "unknown"
        };
    }
}
