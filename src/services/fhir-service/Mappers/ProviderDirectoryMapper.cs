using System.Text.RegularExpressions;
using FhirService.Models;

namespace FhirService.Mappers;

/// <summary>
/// Maps NPPES provider data to FHIR R4 Provider Directory resources.
/// Port of the TypeScript provider-directory-api.ts mapping logic.
/// </summary>
public static partial class ProviderDirectoryMapper
{
    /// <summary>
    /// CMS-assigned prefix for NPI Luhn check digit calculation.
    /// </summary>
    private const string NpiLuhnPrefix = "80840";

    // ── NPI Validation ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates NPI format (10 digits) and Luhn check digit.
    /// </summary>
    public static bool ValidateNpi(string npi)
    {
        if (!NpiRegex().IsMatch(npi))
            return false;

        return LuhnCheck(NpiLuhnPrefix + npi);
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex NpiRegex();

    private static bool LuhnCheck(string value)
    {
        var sum = 0;
        var isEven = false;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var digit = value[i] - '0';
            if (isEven)
            {
                digit *= 2;
                if (digit > 9)
                    digit -= 9;
            }
            sum += digit;
            isEven = !isEven;
        }
        return sum % 10 == 0;
    }

    // ── NPPES → Practitioner ─────────────────────────────────────────────────

    /// <summary>
    /// Maps an NPI-1 (individual) NPPES result to a FHIR Practitioner resource.
    /// </summary>
    [Obsolete("Replaced by provider-service /fhir/Practitioner projection (capability 5.7). " +
              "This method remains only because ProviderDirectoryMapperTests still exercises it. " +
              "Will be removed once 5.8/5.9 retire the NPPES path entirely.")]
    public static FhirPractitioner MapNppesToPractitioner(NppesResult nppes)
    {
        if (nppes.EnumerationType != "NPI-1")
            throw new InvalidOperationException(
                "Cannot map NPI-2 (organization) to Practitioner. Use MapNppesToOrganization instead.");

        return new FhirPractitioner
        {
            Id = nppes.Number,
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitioner"]
            },
            Identifier =
            [
                new FhirIdentifier
                {
                    System = "http://hl7.org/fhir/sid/us-npi",
                    Value = nppes.Number
                }
            ],
            Active = !HasDeactivationDate(nppes) || HasReactivationDate(nppes),
            Name = [MapToHumanName(nppes.Basic)],
            Gender = MapGender(nppes.Basic.Gender),
            Address = MapAddresses(nppes.Addresses),
            Telecom = MapTelecom(nppes.Addresses),
            Qualification = MapQualifications(nppes)
        };
    }

    // ── NPPES → Organization ─────────────────────────────────────────────────

    /// <summary>
    /// Maps an NPI-2 (organizational) NPPES result to a FHIR Organization resource.
    ///
    /// <para>
    /// <b>Deprecated (capability 5.9).</b> The Organization projection is now
    /// served from provider-service's CHO-canonical
    /// <c>FhirOrganizationProjector</c>; <c>ProviderDirectoryController</c>
    /// proxies <c>/fhir/r4/Organization/*</c> there. This helper is retained
    /// until a subsequent cleanup PR removes all NPPES helpers. The Location
    /// path (NPPES) retains a dependency on <c>SearchNppesAsync</c> /
    /// <c>LookupNppesAsync</c>; those are not removed here.
    /// </para>
    /// </summary>
    [Obsolete("Replaced by provider-service FhirOrganizationProjector (capability 5.9). " +
              "Remove in the subsequent NPPES-cleanup PR.")]
    public static FhirOrganization MapNppesToOrganization(NppesResult nppes)
    {
        if (nppes.EnumerationType != "NPI-2")
            throw new InvalidOperationException(
                "Cannot map NPI-1 (individual) to Organization. Use MapNppesToPractitioner instead.");

        return new FhirOrganization
        {
            Id = nppes.Number,
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-organization"]
            },
            Identifier =
            [
                new FhirIdentifier
                {
                    System = "http://hl7.org/fhir/sid/us-npi",
                    Value = nppes.Number
                }
            ],
            Active = !HasDeactivationDate(nppes) || HasReactivationDate(nppes),
            Type = MapOrganizationType(nppes.Taxonomies),
            Name = nppes.Basic.OrganizationName ?? "",
            Alias = nppes.OtherNames?
                .Select(n => n.OrganizationName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Cast<string>()
                .ToList(),
            Address = MapAddresses(nppes.Addresses),
            Telecom = MapTelecom(nppes.Addresses)
        };
    }

    // ── NPPES → PractitionerRole ─────────────────────────────────────────────

    /// <summary>
    /// Maps an NPI-1 NPPES result to a FHIR PractitionerRole resource.
    ///
    /// <para>
    /// <b>Deprecated (capability 5.8).</b> The PractitionerRole projection
    /// is now served from provider-service's CHO-canonical
    /// <c>FhirPractitionerRoleProjector</c>; <c>ProviderDirectoryController</c>
    /// proxies <c>/fhir/r4/PractitionerRole/*</c> there. This helper is
    /// retained until capability 5.9 retires the NPPES helpers wholesale,
    /// then deleted alongside <c>MapNppesToOrganization</c> and
    /// <c>MapNppesToLocation</c>.
    /// </para>
    /// </summary>
    [Obsolete("Replaced by provider-service FhirPractitionerRoleProjector (capability 5.8). " +
              "Removed alongside the rest of the NPPES path in capability 5.9.")]
    public static FhirPractitionerRole MapNppesToPractitionerRole(
        NppesResult practitionerNppes,
        FhirReference? organizationRef = null)
    {
        return new FhirPractitionerRole
        {
            Id = $"{practitionerNppes.Number}-role",
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-practitionerrole"]
            },
            Active = !HasDeactivationDate(practitionerNppes) || HasReactivationDate(practitionerNppes),
            Practitioner = new FhirReference
            {
                Reference = $"Practitioner/{practitionerNppes.Number}",
                Display = FormatProviderName(practitionerNppes.Basic)
            },
            Organization = organizationRef,
            Code = MapTaxonomiesToRoleCode(practitionerNppes.Taxonomies),
            Specialty = MapTaxonomiesToSpecialty(practitionerNppes.Taxonomies),
            Location = MapToLocationReferences(practitionerNppes),
            Telecom = MapTelecom(practitionerNppes.Addresses)
        };
    }

    // ── NPPES → Location ─────────────────────────────────────────────────────

    /// <summary>
    /// Maps an NPPES result address to a FHIR Location resource.
    /// </summary>
    public static FhirLocation MapNppesToLocation(NppesResult nppes, int addressIndex = 0)
    {
        var address = nppes.Addresses.FirstOrDefault(a => a.AddressPurpose == "LOCATION");
        if (address is null && addressIndex < nppes.Addresses.Count)
            address = nppes.Addresses[addressIndex];

        if (address is null)
            throw new InvalidOperationException(
                $"No address found for NPI {nppes.Number} at index {addressIndex}. Available addresses: {nppes.Addresses.Count}");

        return new FhirLocation
        {
            Id = $"{nppes.Number}-loc-{addressIndex}",
            Meta = new FhirMeta
            {
                Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-location"]
            },
            Status = (!HasDeactivationDate(nppes) || HasReactivationDate(nppes)) ? "active" : "inactive",
            Name = nppes.EnumerationType == "NPI-2"
                ? nppes.Basic.OrganizationName
                : FormatProviderName(nppes.Basic),
            Mode = "instance",
            Type = MapLocationTypeFromTaxonomy(nppes.Taxonomies),
            Telecom = MapAddressToTelecom(address),
            Address = MapAddressToFhir(address),
            ManagingOrganization = nppes.EnumerationType == "NPI-2"
                ? new FhirReference { Reference = $"Organization/{nppes.Number}" }
                : null
        };
    }

    // ── Search Bundle ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a FHIR searchset Bundle from a list of resources.
    /// </summary>
    public static FhirSearchBundle CreateSearchBundle(string resourceType, IReadOnlyList<FhirResource> resources)
    {
        return new FhirSearchBundle
        {
            Total = resources.Count,
            Link =
            [
                new FhirBundleLink
                {
                    Relation = "self",
                    Url = $"{resourceType}?_count={resources.Count}"
                }
            ],
            Entry = resources.Select(r => new FhirBundleEntryWithSearch
            {
                FullUrl = $"{resourceType}/{r.Id}",
                Resource = r,
                Search = new FhirBundleSearch { Mode = "match" }
            }).ToList()
        };
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static bool HasDeactivationDate(NppesResult nppes)
        => !string.IsNullOrEmpty(nppes.Basic.DeactivationDate);

    private static bool HasReactivationDate(NppesResult nppes)
        => !string.IsNullOrEmpty(nppes.Basic.ReactivationDate);

    private static FhirHumanName MapToHumanName(NppesBasicInfo basic)
    {
        var given = new List<string>();
        if (!string.IsNullOrEmpty(basic.FirstName)) given.Add(basic.FirstName);
        if (!string.IsNullOrEmpty(basic.MiddleName)) given.Add(basic.MiddleName);

        var suffix = new List<string>();
        if (!string.IsNullOrEmpty(basic.NameSuffix)) suffix.Add(basic.NameSuffix);
        if (!string.IsNullOrEmpty(basic.Credential)) suffix.Add(basic.Credential);

        return new FhirHumanName
        {
            Use = "official",
            Family = basic.LastName ?? "",
            Given = given,
            Prefix = !string.IsNullOrEmpty(basic.NamePrefix) ? [basic.NamePrefix] : null,
            Suffix = suffix.Count > 0 ? suffix : null
        };
    }

    private static string MapGender(string? gender)
    {
        return gender?.ToUpperInvariant() switch
        {
            "M" => "male",
            "F" => "female",
            _ => "unknown"
        };
    }

    private static List<FhirAddress> MapAddresses(IReadOnlyList<NppesAddress> addresses)
    {
        return addresses.Select(MapAddressToFhir).ToList();
    }

    private static FhirAddress MapAddressToFhir(NppesAddress addr)
    {
        var lines = new List<string> { addr.Address1 };
        if (!string.IsNullOrEmpty(addr.Address2))
            lines.Add(addr.Address2);

        return new FhirAddress
        {
            Use = addr.AddressPurpose == "LOCATION" ? "work" : "billing",
            Type = "physical",
            Line = lines,
            City = addr.City,
            State = addr.State,
            PostalCode = addr.PostalCode,
            Country = addr.CountryCode
        };
    }

    private static List<FhirContactPoint> MapTelecom(IReadOnlyList<NppesAddress> addresses)
    {
        var telecom = new List<FhirContactPoint>();
        foreach (var addr in addresses)
        {
            if (!string.IsNullOrEmpty(addr.TelephoneNumber))
            {
                telecom.Add(new FhirContactPoint
                {
                    System = "phone",
                    Value = FormatPhoneNumber(addr.TelephoneNumber),
                    Use = "work"
                });
            }
            if (!string.IsNullOrEmpty(addr.FaxNumber))
            {
                telecom.Add(new FhirContactPoint
                {
                    System = "fax",
                    Value = FormatPhoneNumber(addr.FaxNumber),
                    Use = "work"
                });
            }
        }
        return telecom;
    }

    private static List<FhirContactPoint> MapAddressToTelecom(NppesAddress addr)
    {
        var telecom = new List<FhirContactPoint>();
        if (!string.IsNullOrEmpty(addr.TelephoneNumber))
        {
            telecom.Add(new FhirContactPoint
            {
                System = "phone",
                Value = FormatPhoneNumber(addr.TelephoneNumber),
                Use = "work"
            });
        }
        if (!string.IsNullOrEmpty(addr.FaxNumber))
        {
            telecom.Add(new FhirContactPoint
            {
                System = "fax",
                Value = FormatPhoneNumber(addr.FaxNumber),
                Use = "work"
            });
        }
        return telecom;
    }

    private static string FormatPhoneNumber(string phone)
    {
        var digits = DigitsOnly().Replace(phone, "");
        if (digits.Length == 10)
            return $"{digits[..3]}-{digits[3..6]}-{digits[6..]}";
        return phone;
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();

    private static List<FhirQualification> MapQualifications(NppesResult nppes)
    {
        return nppes.Taxonomies.Select(tax => new FhirQualification
        {
            Identifier =
            [
                new FhirIdentifier
                {
                    System = "http://nucc.org/provider-taxonomy",
                    Value = tax.Code
                }
            ],
            Code = new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://nucc.org/provider-taxonomy",
                        Code = tax.Code,
                        Display = tax.Desc
                    }
                ]
            },
            Period = !string.IsNullOrEmpty(tax.License)
                ? new FhirPeriod { Start = nppes.Basic.EnumerationDate }
                : null,
            Issuer = !string.IsNullOrEmpty(tax.State)
                ? new FhirReference { Display = $"State of {tax.State}" }
                : null
        }).ToList();
    }

    private static List<FhirCodeableConcept> MapOrganizationType(IReadOnlyList<NppesTaxonomy> taxonomies)
    {
        if (taxonomies.Count == 0)
        {
            return
            [
                new FhirCodeableConcept
                {
                    Coding =
                    [
                        new FhirCoding
                        {
                            System = "http://terminology.hl7.org/CodeSystem/organization-type",
                            Code = "prov",
                            Display = "Healthcare Provider"
                        }
                    ]
                }
            ];
        }

        return taxonomies.Select(tax => new FhirCodeableConcept
        {
            Coding =
            [
                new FhirCoding
                {
                    System = "http://nucc.org/provider-taxonomy",
                    Code = tax.Code,
                    Display = tax.Desc
                }
            ]
        }).ToList();
    }

    private static List<FhirCodeableConcept> MapTaxonomiesToRoleCode(IReadOnlyList<NppesTaxonomy> taxonomies)
    {
        var primary = taxonomies.FirstOrDefault(t => t.Primary) ?? taxonomies.FirstOrDefault();
        if (primary is null)
            return [];

        return
        [
            new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://nucc.org/provider-taxonomy",
                        Code = primary.Code,
                        Display = primary.Desc
                    }
                ]
            }
        ];
    }

    private static List<FhirCodeableConcept> MapTaxonomiesToSpecialty(IReadOnlyList<NppesTaxonomy> taxonomies)
    {
        return taxonomies.Select(tax => new FhirCodeableConcept
        {
            Coding =
            [
                new FhirCoding
                {
                    System = "http://nucc.org/provider-taxonomy",
                    Code = tax.Code,
                    Display = tax.Desc
                }
            ]
        }).ToList();
    }

    private static List<FhirReference> MapToLocationReferences(NppesResult nppes)
    {
        return nppes.Addresses
            .Where(a => a.AddressPurpose == "LOCATION")
            .Select((_, index) => new FhirReference
            {
                Reference = $"Location/{nppes.Number}-loc-{index}"
            })
            .ToList();
    }

    private static List<FhirCodeableConcept> MapLocationTypeFromTaxonomy(IReadOnlyList<NppesTaxonomy> taxonomies)
    {
        var primary = taxonomies.FirstOrDefault(t => t.Primary) ?? taxonomies.FirstOrDefault();
        var isHospital = primary?.Desc?.Contains("hospital", StringComparison.OrdinalIgnoreCase) == true;

        return
        [
            new FhirCodeableConcept
            {
                Coding =
                [
                    new FhirCoding
                    {
                        System = "http://terminology.hl7.org/CodeSystem/v3-RoleCode",
                        Code = isHospital ? "HOSP" : "OF",
                        Display = isHospital ? "Hospital" : "Outpatient facility"
                    }
                ]
            }
        ];
    }

    private static string FormatProviderName(NppesBasicInfo basic)
    {
        if (!string.IsNullOrEmpty(basic.OrganizationName))
            return basic.OrganizationName;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(basic.FirstName)) parts.Add(basic.FirstName);
        if (!string.IsNullOrEmpty(basic.MiddleName)) parts.Add(basic.MiddleName);
        if (!string.IsNullOrEmpty(basic.LastName)) parts.Add(basic.LastName);
        if (!string.IsNullOrEmpty(basic.Credential)) parts.Add(basic.Credential);
        return string.Join(' ', parts);
    }

    // ── Provider Verification Enrichment ─────────────────────────────────────

    /// <summary>
    /// Enriches a Practitioner resource with verification metadata from the
    /// Provider Verification Service. Adds an extension with integrity score,
    /// rating, and exclusion status. Sets active=false for excluded providers.
    /// </summary>
    [Obsolete("Practitioner verification enrichment now lives on the provider-service projection " +
              "as the cho-provider-integrity-score extension (capability 5.4.5 / 5.7). " +
              "This method remains only because ProviderDirectoryVerificationTests still exercises it. " +
              "Will be removed once 5.8/5.9 retire the NPPES path entirely.")]
    public static void EnrichWithVerification(
        FhirPractitioner practitioner, ProviderVerificationSummary verification)
    {
        practitioner.Extension ??= new List<FhirExtension>();
        practitioner.Extension.Add(new FhirExtension
        {
            Url = "https://cloudhealthoffice.com/fhir/StructureDefinition/provider-verification",
            Extension = new List<FhirExtension>
            {
                new() { Url = "integrityScore", ValueInteger = verification.IntegrityScore },
                new() { Url = "rating", ValueString = verification.Rating },
                new() { Url = "status", ValueString = verification.Status },
                new() { Url = "isExcluded", ValueBoolean = verification.IsExcluded },
            },
        });

        if (verification.IsExcluded)
        {
            practitioner.Active = false;
        }
    }
}
