using System.Globalization;
using System.Text.Json.Nodes;
using AuthorizationService.Models;

namespace AuthorizationService.Services.Fhir;

/// <summary>
/// Projects an authoritative <see cref="Authorization"/> record to a FHIR R4
/// <c>ExplanationOfBenefit</c> with <c>use = preauthorization</c> — the shape
/// CMS-0057-F requires for prior-authorization data in the Patient Access,
/// Provider Access and Payer-to-Payer APIs, and the shape Da Vinci PDex
/// defines for it.
///
/// <para>
/// authorization-service owns this projection, as claims-service owns the
/// claim <c>ExplanationOfBenefit</c> projection: there is one prior-auth store
/// and one place that turns it into FHIR. fhir-service proxies to it and adds
/// nothing of its own.
/// </para>
///
/// <para>
/// What CMS-0057-F asks for and what this carries: the authorization's
/// <b>status</b> (<c>outcome</c> + <c>disposition</c>), its <b>dates</b>
/// (<c>created</c>, <c>billablePeriod</c> requested, <c>benefitPeriod</c>
/// approved), its <b>items</b> (<c>item[].productOrService</c> with modifiers),
/// the <b>quantity</b> requested and approved (<c>item[].quantity</c> and the
/// unit adjudications), and the <b>denial reason</b>, coded and human-readable,
/// at header and line level.
/// </para>
///
/// <para>
/// <b>Stated conformance limitations.</b> This is not a validated PDex
/// prior-authorization resource, and the profile is declared as what CHO
/// implements rather than as a conformance claim:
/// </para>
/// <list type="bullet">
///   <item><c>type</c> is the FHIR claim-type <c>other</c>. CHO's prior-auth
///   record carries an X12 service-type code, not a claim type, and this
///   projector will not infer institutional-versus-professional from data that
///   does not say it. The service type travels in <c>type.text</c> and in
///   <c>subType</c> under the X12 1365 system.</item>
///   <item><c>insurer</c> is a reference with a display only: CHO has no payer
///   <c>Organization</c> resource to point at. The claim EOB projection has the
///   same limitation.</item>
///   <item>Unit adjudication uses a CHO-private category system. FHIR's
///   adjudication value set has no code for "units requested" or "units
///   approved", and this projector will not borrow a monetary code for a
///   quantity.</item>
///   <item>No PDex item-level extensions (item trace number, item
///   pre-auth issue date), and no <c>careTeam</c> or <c>supportingInfo</c>.</item>
/// </list>
/// </summary>
public interface IPriorAuthorizationEobProjector
{
    /// <summary>The FHIR resource id this projector gives an authorization.</summary>
    string ResourceId(Authorization authorization);

    JsonObject Project(Authorization authorization);
}

public sealed class PriorAuthorizationEobProjector : IPriorAuthorizationEobProjector
{
    /// <summary>
    /// Prefix on the FHIR resource id. An id is
    /// <c>pa-{authorizationNumber}</c>, which keeps prior-auth EOB ids disjoint
    /// from claim EOB ids (claim-version ids) in the one
    /// <c>ExplanationOfBenefit</c> namespace both projections share, so
    /// fhir-service can route a read to the right owning service from the id
    /// alone.
    /// </summary>
    public const string ResourceIdPrefix = "pa-";

    /// <summary>
    /// The PDex prior-authorization profile this projection is shaped toward.
    /// Declared so a consumer can see what CHO is implementing; see the
    /// limitations on <see cref="IPriorAuthorizationEobProjector"/> — no
    /// profile validation has run against it.
    /// </summary>
    public const string PdexPriorAuthorizationProfile =
        "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-prior-authorization";

    private const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";
    private const string X12ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";
    private const string HcpcsSystem = "https://bluebutton.cms.gov/resources/codesystem/hcpcs";
    private const string ModifierSystem = "http://www.ama-assn.org/go/cpt";

    /// <summary>
    /// Identifier type for the authorization number: V2-0203 <c>FILL</c> is not
    /// right and there is no standard "prior authorization number" type, so the
    /// identifier carries the CHO system without a type coding rather than a
    /// borrowed one.
    /// </summary>
    private const string AuthorizationNumberSystem = "urn:cho:authorization-number";

    /// <summary>
    /// CHO-private adjudication categories for unit quantities. FHIR's
    /// adjudication value set codes monetary outcomes; none of them means
    /// "units requested" or "units approved".
    /// </summary>
    private const string UnitAdjudicationSystem = "urn:cho:pa-unit-adjudication";

    /// <summary>
    /// CHO-private system for the authorization's own denial reason codes. These
    /// are CHO/278 review codes (e.g. <c>NOTMEDNEC</c>), not X12 CARCs, so they
    /// do not go under the CARC system URI the claim projection uses.
    /// </summary>
    private const string DenialReasonSystem = "urn:cho:pa-denial-reason";

    public string ResourceId(Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return ResourceIdPrefix + authorization.AuthorizationNumber;
    }

    /// <summary>
    /// The authorization number inside a prior-auth EOB resource id, or null
    /// when the id is not one of ours. The inverse of
    /// <see cref="ResourceId"/>.
    /// </summary>
    public static string? AuthorizationNumberFromResourceId(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        var trimmed = resourceId.Trim();
        if (!trimmed.StartsWith(ResourceIdPrefix, StringComparison.Ordinal)) return null;
        var number = trimmed[ResourceIdPrefix.Length..];
        return number.Length == 0 ? null : number;
    }

    public JsonObject Project(Authorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var eob = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = ResourceId(authorization),
            ["meta"] = new JsonObject
            {
                ["lastUpdated"] = Instant(authorization.LastUpdatedDate),
                ["profile"] = new JsonArray { PdexPriorAuthorizationProfile },
            },
            // FHIR EOB.status is the resource's own lifecycle, not the review
            // decision: a withdrawn request is `cancelled`, everything else is
            // an active record. The decision travels in outcome/disposition.
            ["status"] = authorization.Status == AuthorizationStatus.Cancelled ? "cancelled" : "active",
            ["type"] = BuildType(authorization),
            // The discriminator CMS-0057-F consumers search on, and what makes
            // this resource a prior authorization rather than a claim.
            ["use"] = "preauthorization",
            ["patient"] = new JsonObject
            {
                ["reference"] = "Patient/" + authorization.MemberId,
            },
            ["created"] = Instant(authorization.SubmittedDate),
            ["insurer"] = new JsonObject
            {
                // No payer Organization resource exists to reference (see the
                // limitations on the interface).
                ["display"] = "Cloud Health Office",
            },
            ["provider"] = BuildProvider(authorization),
            ["outcome"] = Outcome(authorization.Status),
        };

        if (!string.IsNullOrWhiteSpace(authorization.AuthorizationNumber))
        {
            eob["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = AuthorizationNumberSystem,
                    ["value"] = authorization.AuthorizationNumber,
                },
            };
            // preAuthRef is the number a provider quotes on the claim. FHIR puts
            // it on insurance[]; it is also the identifier above.
            eob["insurance"] = new JsonArray
            {
                BuildInsurance(authorization),
            };
        }
        else
        {
            eob["insurance"] = new JsonArray { BuildInsurance(authorization) };
        }

        var subType = BuildSubType(authorization);
        if (subType is not null) eob["subType"] = subType;

        var disposition = Disposition(authorization);
        if (disposition is not null) eob["disposition"] = disposition;

        var requested = Period(authorization.RequestedServiceDateFrom, authorization.RequestedServiceDateTo);
        if (requested is not null) eob["billablePeriod"] = requested;

        // The approved period, which is what an approval actually grants. Falls
        // back to the expiration date when an end date was not approved
        // explicitly, because that is the date the grant stops.
        var benefit = Period(
            authorization.ApprovedServiceDateFrom,
            authorization.ApprovedServiceDateTo ?? authorization.ExpirationDate);
        if (benefit is not null) eob["benefitPeriod"] = benefit;

        var diagnoses = BuildDiagnoses(authorization);
        if (diagnoses.Count > 0) eob["diagnosis"] = diagnoses;

        var items = BuildItems(authorization);
        if (items.Count > 0) eob["item"] = items;

        var notes = BuildProcessNotes(authorization);
        if (notes.Count > 0) eob["processNote"] = notes;

        return eob;
    }

    // ── pieces ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>type</c> is required by base FHIR and CHO's record does not carry a
    /// claim type, so it is the honest <c>other</c> rather than a guess. The
    /// X12 service type, which the record does carry, goes in
    /// <c>type.text</c> and in <see cref="BuildSubType"/>.
    /// </summary>
    private static JsonObject BuildType(Authorization authorization)
    {
        var type = new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = ClaimTypeSystem,
                    ["code"] = "other",
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(authorization.ServiceTypeCode))
            type["text"] = "X12 service type " + authorization.ServiceTypeCode;

        return type;
    }

    private static JsonObject? BuildSubType(Authorization authorization)
        => string.IsNullOrWhiteSpace(authorization.ServiceTypeCode)
            ? null
            : new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = X12ServiceTypeSystem,
                        ["code"] = authorization.ServiceTypeCode,
                    },
                },
            };

    /// <summary>
    /// The requesting provider: the NPI as a reference identifier, because CHO
    /// has no Practitioner resource id for it here. The servicing provider,
    /// where it differs, is not a second <c>provider</c> (FHIR allows one) and
    /// is surfaced as a process note instead.
    /// </summary>
    private static JsonObject BuildProvider(Authorization authorization)
    {
        var provider = new JsonObject();
        if (!string.IsNullOrWhiteSpace(authorization.RequestingProviderNPI))
        {
            provider["identifier"] = new JsonObject
            {
                ["system"] = NpiSystem,
                ["value"] = authorization.RequestingProviderNPI,
            };
        }
        provider["display"] = string.IsNullOrWhiteSpace(authorization.RequestingProviderName)
            ? "Requesting provider"
            : authorization.RequestingProviderName;
        return provider;
    }

    private static JsonObject BuildInsurance(Authorization authorization)
    {
        var insurance = new JsonObject
        {
            ["focal"] = true,
            ["coverage"] = string.IsNullOrWhiteSpace(authorization.CoverageId)
                ? new JsonObject { ["display"] = "Member coverage" }
                : new JsonObject { ["reference"] = "Coverage/" + authorization.CoverageId },
        };

        if (!string.IsNullOrWhiteSpace(authorization.AuthorizationNumber))
            insurance["preAuthRef"] = new JsonArray { authorization.AuthorizationNumber };

        return insurance;
    }

    private static JsonArray BuildDiagnoses(Authorization authorization)
    {
        var diagnoses = new JsonArray();
        var sequence = 1;
        foreach (var diagnosis in authorization.DiagnosisCodes)
        {
            if (string.IsNullOrWhiteSpace(diagnosis.Code)) continue;

            var coding = new JsonObject
            {
                ["system"] = Icd10CmSystem,
                ["code"] = diagnosis.Code,
            };
            if (!string.IsNullOrWhiteSpace(diagnosis.Description))
                coding["display"] = diagnosis.Description;

            diagnoses.Add(new JsonObject
            {
                ["sequence"] = sequence++,
                ["diagnosisCodeableConcept"] = new JsonObject
                {
                    ["coding"] = new JsonArray { coding },
                },
            });
        }
        return diagnoses;
    }

    private static JsonArray BuildItems(Authorization authorization)
    {
        var items = new JsonArray();
        var sequence = 1;

        foreach (var service in authorization.RequestedServices)
        {
            if (string.IsNullOrWhiteSpace(service.ProcedureCode)) continue;

            // The line's own number when it has one, so a consumer can line the
            // item up with the 278 it submitted; otherwise positional.
            var itemSequence = service.LineNumber is > 0 ? service.LineNumber!.Value : sequence;
            sequence++;

            var item = new JsonObject
            {
                ["sequence"] = itemSequence,
                ["productOrService"] = BuildProductOrService(service),
            };

            var modifiers = BuildModifiers(service);
            if (modifiers.Count > 0) item["modifier"] = modifiers;

            var quantity = BuildQuantity(service);
            if (quantity is not null) item["quantity"] = quantity;

            var adjudication = BuildItemAdjudication(service);
            if (adjudication.Count > 0) item["adjudication"] = adjudication;

            items.Add(item);
        }

        return items;
    }

    private static JsonObject BuildProductOrService(RequestedService service)
    {
        var coding = new JsonObject
        {
            ["system"] = ProcedureSystem(service.ProductOrServiceSystem, service.ProcedureCode),
            ["code"] = service.ProcedureCode,
        };
        if (!string.IsNullOrWhiteSpace(service.ProcedureDescription))
            coding["display"] = service.ProcedureDescription;

        return new JsonObject { ["coding"] = new JsonArray { coding } };
    }

    /// <summary>
    /// The code system for a requested procedure. An explicit hint on the line
    /// wins; otherwise a 5-character all-digit code is CPT and anything else
    /// starting with a letter is HCPCS, which is how CHO's own 278 intake reads
    /// these. An unrecognised shape keeps the hint verbatim rather than being
    /// relabelled.
    /// </summary>
    internal static string ProcedureSystem(string? hint, string code)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            var normalized = hint.Trim();
            if (normalized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return normalized;

            return normalized.ToLowerInvariant() switch
            {
                "cpt" => CptSystem,
                "hcpcs" => HcpcsSystem,
                _ => normalized,
            };
        }

        var trimmed = code.Trim();
        if (trimmed.Length == 5 && trimmed.All(char.IsAsciiDigit)) return CptSystem;
        if (trimmed.Length == 5 && char.IsAsciiLetter(trimmed[0])) return HcpcsSystem;
        return CptSystem;
    }

    private static JsonArray BuildModifiers(RequestedService service)
    {
        var modifiers = new JsonArray();
        foreach (var modifier in service.Modifiers)
        {
            if (string.IsNullOrWhiteSpace(modifier)) continue;
            modifiers.Add(new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = ModifierSystem,
                        ["code"] = modifier.Trim(),
                    },
                },
            });
        }
        return modifiers;
    }

    private static JsonObject? BuildQuantity(RequestedService service)
    {
        var quantity = new JsonObject
        {
            ["value"] = Decimal(service.RequestedUnits),
        };

        if (!string.IsNullOrWhiteSpace(service.UnitType))
            quantity["unit"] = service.UnitType;
        if (!string.IsNullOrWhiteSpace(service.UnitSystem))
            quantity["system"] = service.UnitSystem;
        if (!string.IsNullOrWhiteSpace(service.UnitCode))
            quantity["code"] = service.UnitCode;

        return quantity;
    }

    /// <summary>
    /// What was requested, what was approved, and why a line was refused.
    /// The unit categories are CHO-private (see the interface limitations); the
    /// denial reason is a <c>reason</c> coding plus its text.
    /// </summary>
    private static JsonArray BuildItemAdjudication(RequestedService service)
    {
        var adjudication = new JsonArray
        {
            UnitAdjudication("requested-units", "Units requested", service.RequestedUnits),
        };

        if (service.ApprovedUnits is { } approved)
            adjudication.Add(UnitAdjudication("approved-units", "Units approved", approved));

        if (!string.IsNullOrWhiteSpace(service.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(service.DenialReason))
        {
            var denial = new JsonObject
            {
                ["category"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = UnitAdjudicationSystem,
                            ["code"] = "denial-reason",
                            ["display"] = "Reason the line was denied or reduced",
                        },
                    },
                },
            };

            if (!string.IsNullOrWhiteSpace(service.DenialReasonCode))
            {
                var reason = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = DenialReasonSystem,
                            ["code"] = service.DenialReasonCode,
                        },
                    },
                };
                if (!string.IsNullOrWhiteSpace(service.DenialReason))
                    reason["text"] = service.DenialReason;
                denial["reason"] = reason;
            }
            else
            {
                denial["reason"] = new JsonObject { ["text"] = service.DenialReason };
            }

            adjudication.Add(denial);
        }

        return adjudication;
    }

    private static JsonObject UnitAdjudication(string code, string display, decimal value)
        => new()
        {
            ["category"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = UnitAdjudicationSystem,
                        ["code"] = code,
                        ["display"] = display,
                    },
                },
            },
            ["value"] = Decimal(value),
        };

    /// <summary>
    /// Header-level narrative a member or provider can read: the denial reason,
    /// why the request is pended, what happens next, and the servicing provider
    /// when it is not the requesting one.
    /// </summary>
    private static JsonArray BuildProcessNotes(Authorization authorization)
    {
        var notes = new JsonArray();
        var number = 1;

        void Add(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            notes.Add(new JsonObject
            {
                ["number"] = number++,
                ["type"] = "display",
                ["text"] = text,
            });
        }

        if (!string.IsNullOrWhiteSpace(authorization.DenialReason))
        {
            Add(string.IsNullOrWhiteSpace(authorization.DenialReasonCode)
                ? authorization.DenialReason!
                : $"{authorization.DenialReasonCode}: {authorization.DenialReason}");
        }
        else if (!string.IsNullOrWhiteSpace(authorization.DenialReasonCode))
        {
            Add("Denial reason code " + authorization.DenialReasonCode);
        }

        if (!string.IsNullOrWhiteSpace(authorization.PendReason))
            Add("Pended: " + authorization.PendReason);

        if (!string.IsNullOrWhiteSpace(authorization.FollowUpAction))
            Add("Next step: " + authorization.FollowUpAction);

        if (!string.IsNullOrWhiteSpace(authorization.ServicingProviderNPI)
            && !string.Equals(authorization.ServicingProviderNPI, authorization.RequestingProviderNPI, StringComparison.Ordinal))
        {
            Add("Servicing provider NPI " + authorization.ServicingProviderNPI
                + (string.IsNullOrWhiteSpace(authorization.ServicingProviderName)
                    ? string.Empty
                    : " (" + authorization.ServicingProviderName + ")"));
        }

        return notes;
    }

    // ── mapping helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// FHIR <c>remittance-outcome</c>: a decision that has been made is
    /// <c>complete</c>, a partial approval is <c>partial</c>, and anything still
    /// in flight is <c>queued</c>. A denial is a completed decision, not an
    /// error — <c>error</c> would mean the request could not be processed.
    /// </summary>
    internal static string Outcome(AuthorizationStatus status) => status switch
    {
        AuthorizationStatus.Approved => "complete",
        AuthorizationStatus.Denied => "complete",
        AuthorizationStatus.Expired => "complete",
        AuthorizationStatus.Cancelled => "complete",
        AuthorizationStatus.Modified => "partial",
        _ => "queued",
    };

    /// <summary>Plain-language decision, which is what <c>disposition</c> is for.</summary>
    internal static string? Disposition(Authorization authorization) => authorization.Status switch
    {
        AuthorizationStatus.Approved => "Approved",
        AuthorizationStatus.Modified => "Approved in part",
        AuthorizationStatus.Denied => "Denied",
        AuthorizationStatus.Expired => "Approval period has ended",
        AuthorizationStatus.Cancelled => "Withdrawn",
        AuthorizationStatus.Pended => "Pended for additional information",
        AuthorizationStatus.InReview => "Under review",
        AuthorizationStatus.Submitted => "Received",
        _ => null,
    };

    private static string Instant(DateTime value)
        => DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind)
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);

    private static string Date(DateTime value)
        => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonObject? Period(DateTime? start, DateTime? end)
    {
        if (start is null && end is null) return null;
        var period = new JsonObject();
        if (start is { } from) period["start"] = Date(from);
        if (end is { } to) period["end"] = Date(to);
        return period;
    }

    /// <summary>
    /// JSON numbers for FHIR decimals: trailing zeros removed so a whole number
    /// of units reads as <c>1</c> rather than <c>1.00</c>, which is what a unit
    /// count means.
    /// </summary>
    private static JsonNode Decimal(decimal value)
        => JsonValue.Create(value == decimal.Truncate(value) ? decimal.Truncate(value) : value)!;
}
