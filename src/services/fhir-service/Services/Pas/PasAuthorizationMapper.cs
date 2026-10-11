using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;

namespace FhirService.Services.Pas;

/// <summary>
/// Maps a Da Vinci PAS request Bundle onto the authorization record
/// authorization-service stores (D-01, D-03, D-08). Pure: no I/O, no clock
/// other than the receipt time it is given.
///
/// Every value comes from the request. Nothing is defaulted to a plausible
/// placeholder: a value the record needs and the request lacks is an error
/// (the caller answers 422 and stores nothing); a value the record can hold as
/// unknown (line of business, service type, level of service, servicing
/// provider) is stored as unknown.
///
/// <list type="table">
///   <item><term>Member, name, date of birth</term><description>The Patient
///   <c>Claim.patient</c> references, which must be in the Bundle. Member id
///   from its identifier typed <c>MB</c> (v2-0203), else
///   <c>Coverage.subscriberId</c> when the patient IS the subscriber
///   (<c>relationship</c> = self, or <c>subscriber</c> = <c>beneficiary</c>;
///   for a dependent it is the policy holder's id), else a Coverage
///   identifier typed <c>MB</c>.</description></item>
///   <item><term>Coverage, line of business</term><description>The Coverage
///   the focal <c>Claim.insurance</c> references, which must be in the Bundle.
///   Line of business from <c>Coverage.type</c> (X12 claim filing indicator
///   1032 or Source of Payment Typology, by URL or OID); anything else is
///   <c>Unknown</c>.</description></item>
///   <item><term>Requesting provider</term><description>The NPI
///   (<c>http://hl7.org/fhir/sid/us-npi</c>) of the Practitioner /
///   Organization / PractitionerRole <c>Claim.provider</c> references, or of
///   the reference's own identifier. Required.</description></item>
///   <item><term>Servicing provider</term><description>The NPI of the
///   <c>Claim.careTeam</c> member in a primary/rendering/performing role, when
///   there is one.</description></item>
///   <item><term>Service type (UM03)</term><description><c>Claim.item.category</c>
///   in the X12 service type code system (1365); null when absent. Never the
///   claim type.</description></item>
///   <item><term>Level of service (UM06)</term><description>The PAS
///   level-of-service extension (X12 1338: <c>U</c>, <c>03</c> → <c>U</c>;
///   <c>E</c> → <c>E</c>) on the Claim or an item, else <c>Claim.priority</c>
///   read by its system: processpriority <c>stat</c> → <c>U</c>,
///   <c>normal</c> / <c>deferred</c> → <c>E</c>; X12 1338 as above. A code
///   in no system or another system, or none → null, the standard
///   clock.</description></item>
///   <item><term>Requested dates</term><description><c>Claim.item.servicedDate</c>
///   / <c>servicedPeriod</c> across all items, else <c>Claim.billablePeriod</c>.
///   Required.</description></item>
///   <item><term>Approved period (for an approval)</term><description>The
///   requested period. A single date (or several) approves that date range.
///   Only when the request gives an open-ended period (a start and no end)
///   does the end fall back to start + 1 year (<see cref="OpenEndedApproval"/>),
///   or to the latest end another service names if that is later; the PAS IG
///   sets no default.</description></item>
///   <item><term>Codes</term><description>Each item's
///   <c>productOrService</c> code with its system, units, modifiers, place of
///   service and revenue code; diagnoses by their ICD-10-CM coding (else
///   ICD-10), a diagnosis with neither being skipped.</description></item>
/// </list>
/// </summary>
public static class PasAuthorizationMapper
{
    public const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    public const string X12ServiceTypeSystem = "https://codesystem.x12.org/005010/1365";
    public const string X12ClaimFilingIndicatorSystem = "https://codesystem.x12.org/005010/1032";
    public const string SourceOfPaymentTypologySystem = "https://nahdo.org/sopt";
    public const string SourceOfPaymentTypologyOid = "urn:oid:2.16.840.1.113883.3.221.5";
    public const string ProcessPrioritySystem = "http://terminology.hl7.org/CodeSystem/processpriority";
    public const string X12LevelOfServiceSystem = "https://codesystem.x12.org/005010/1338";
    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string Icd10System = "http://hl7.org/fhir/sid/icd-10";
    private const string SubscriberRelationshipSystem = "http://terminology.hl7.org/CodeSystem/subscriber-relationship";
    private const string IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";

    /// <summary>Da Vinci PAS level-of-service extension (X12 UM06, code system 1338).</summary>
    public const string LevelOfServiceExtensionUrl =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-levelOfServiceCode";

    /// <summary>
    /// The end of an approval whose requested period has a start and no end.
    /// Not from the PAS IG (which sets no default); a documented policy
    /// fallback, applied only in that case.
    /// </summary>
    public static readonly TimeSpan OpenEndedApproval = TimeSpan.FromDays(365);

    private static readonly Regex Npi = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly Regex ServiceTypeCode = new(@"^[A-Za-z0-9]{1,2}$", RegexOptions.Compiled);

    public static PasMappingResult Map(Bundle bundle, Claim claim, DateTime receivedAt)
    {
        var errors = new List<PasMappingError>();
        var request = new PasAuthorizationRecordRequest { ReceivedAt = receivedAt };

        MapPatientAndCoverage(bundle, claim, request, errors);
        MapProviders(bundle, claim, request, errors);
        MapServices(bundle, claim, request, errors);

        request.LevelOfService = LevelOfService(claim);
        request.ServiceTypeCode = claim.Item?
            .Select(i => i.Category?.Coding?.FirstOrDefault(c =>
                c.System == X12ServiceTypeSystem && c.Code is not null && ServiceTypeCode.IsMatch(c.Code))?.Code)
            .FirstOrDefault(c => c is not null);

        var period = RequestedPeriod(claim);
        if (period is null)
        {
            errors.Add(new("Claim.item.serviced", "The request names no service date: set Claim.item.servicedDate "
                + "or servicedPeriod (or Claim.billablePeriod)."));
        }
        else
        {
            request.RequestedServiceDateFrom = period.Value.From;
            request.RequestedServiceDateTo = period.Value.To;
        }

        return errors.Count > 0
            ? PasMappingResult.Invalid(errors)
            : PasMappingResult.Valid(request, ApprovedPeriodFor(period!.Value));
    }

    // ── Patient, Coverage ─────────────────────────────────────────────

    private static void MapPatientAndCoverage(
        Bundle bundle, Claim claim, PasAuthorizationRecordRequest request, List<PasMappingError> errors)
    {
        var patient = Resolve(bundle, claim.Patient) as Patient;
        var focal = claim.Insurance?.FirstOrDefault(i => i.Focal == true) ?? claim.Insurance?.FirstOrDefault();
        var coverage = Resolve(bundle, focal?.Coverage) as Coverage;

        if (patient is null)
        {
            errors.Add(new("Claim.patient", "The Patient that Claim.patient references must be in the Bundle."));
        }
        else
        {
            if (claim.Patient?.Reference is { Length: <= 200 } reference)
                request.PatientReference = reference;

            var name = patient.Name?.FirstOrDefault(n => n.Use == HumanName.NameUse.Official)
                       ?? patient.Name?.FirstOrDefault();
            var family = name?.Family?.Trim();
            var given = name?.Given?.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g))?.Trim();
            if (string.IsNullOrEmpty(family) || string.IsNullOrEmpty(given))
                errors.Add(new("Patient.name", "Patient.name needs a family name and a given name."));
            else if (family.Length > 100 || given.Length > 100)
                errors.Add(new("Patient.name", "Patient.name parts are limited to 100 characters."));
            else
            {
                request.PatientLastName = family;
                request.PatientFirstName = given;
            }

            var birthDate = ParseFullDate(patient.BirthDate);
            if (birthDate is null)
                errors.Add(new("Patient.birthDate", "Patient.birthDate must be a full date (YYYY-MM-DD)."));
            else
                request.PatientDateOfBirth = birthDate;
        }

        if (coverage is null)
        {
            errors.Add(new("Claim.insurance.coverage",
                "The Coverage that Claim.insurance.coverage references must be in the Bundle."));
        }
        else
        {
            request.LineOfBusiness = LineOfBusiness(coverage);
            var coverageIdentifier = coverage.Identifier?
                .Select(i => i.Value?.Trim())
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));
            if (coverageIdentifier is { Length: <= 50 })
                request.CoverageId = coverageIdentifier;
        }

        var memberId = MemberIdentifier(patient?.Identifier)
                       ?? (coverage is not null && PatientIsSubscriber(bundle, coverage)
                           ? NullIfEmpty(coverage.SubscriberId?.Trim())
                           : null)
                       ?? MemberIdentifier(coverage?.Identifier);
        if (string.IsNullOrEmpty(memberId))
        {
            if (patient is not null && coverage is not null)
                errors.Add(new("Patient.identifier", "No member identifier: Patient.identifier typed MB, "
                    + "or Coverage.subscriberId, is required."));
        }
        else if (memberId.Length > 50)
        {
            errors.Add(new("Patient.identifier", "The member identifier is limited to 50 characters."));
        }
        else
        {
            request.MemberId = memberId;
        }
    }

    /// <summary>
    /// Whether <c>Coverage.subscriberId</c> is the patient's own: the
    /// relationship is <c>self</c>, or the subscriber is the beneficiary. For a
    /// dependent (or when the Coverage does not say) it is the policy holder's
    /// id, not the patient's.
    /// </summary>
    private static bool PatientIsSubscriber(Bundle bundle, Coverage coverage)
    {
        if (coverage.Relationship?.Coding?.Any(c =>
                c.System == SubscriberRelationshipSystem && c.Code == "self") == true)
            return true;

        if (coverage.Subscriber?.Reference is not { Length: > 0 } subscriber
            || coverage.Beneficiary?.Reference is not { Length: > 0 } beneficiary)
            return false;

        if (string.Equals(subscriber, beneficiary, StringComparison.Ordinal))
            return true;

        var subscriberResource = Resolve(bundle, coverage.Subscriber);
        return subscriberResource is Patient && ReferenceEquals(subscriberResource, Resolve(bundle, coverage.Beneficiary));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? MemberIdentifier(IEnumerable<Identifier>? identifiers)
        => identifiers?
            .FirstOrDefault(i => i.Type?.Coding?.Any(c => c.System == IdentifierTypeSystem && c.Code == "MB") == true
                                 && !string.IsNullOrWhiteSpace(i.Value))
            ?.Value?.Trim();

    /// <summary>
    /// The line of business <c>Coverage.type</c> states, or <c>Unknown</c>.
    /// Never a default: an unrecognised or absent type is unknown, not
    /// Commercial (D-03).
    /// </summary>
    public static string LineOfBusiness(Coverage coverage)
    {
        foreach (var coding in coverage.Type?.Coding ?? [])
        {
            var code = coding.Code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code))
                continue;

            if (coding.System == X12ClaimFilingIndicatorSystem)
            {
                var lob = code switch
                {
                    "MA" or "MB" or "16" => "Medicare",
                    "MC" => "Medicaid",
                    "CH" => "TRICARE",
                    "VA" => "VA",
                    "CI" or "BL" or "HM" or "12" or "13" or "14" or "15" => "Commercial",
                    _ => null,
                };
                if (lob is not null) return lob;
            }
            else if (coding.System is SourceOfPaymentTypologySystem or SourceOfPaymentTypologyOid)
            {
                // SOPT is hierarchical: the leading digits name the family.
                var lob = code switch
                {
                    _ when code.StartsWith("311", StringComparison.Ordinal) => "TRICARE",
                    _ when code.StartsWith("32", StringComparison.Ordinal) => "VA",
                    _ when code.StartsWith('1') => "Medicare",
                    _ when code.StartsWith('2') => "Medicaid",
                    _ when code.StartsWith('5') || code.StartsWith('6') => "Commercial",
                    _ => null,
                };
                if (lob is not null) return lob;
            }
        }

        return "Unknown";
    }

    // ── Providers ─────────────────────────────────────────────────────

    private static void MapProviders(
        Bundle bundle, Claim claim, PasAuthorizationRecordRequest request, List<PasMappingError> errors)
    {
        var (npi, name) = ProviderNpi(bundle, claim.Provider);
        if (npi is null)
        {
            errors.Add(new("Claim.provider", "The requesting provider needs an NPI "
                + $"({NpiSystem}): on the Practitioner or Organization Claim.provider references, or on the reference."));
        }
        else
        {
            request.RequestingProviderNPI = npi;
            request.RequestingProviderName = Truncate(name, 300);
        }

        var servicing = claim.CareTeam?
            .Where(ct => ct.Role?.Coding?.Any(c => c.Code is "primary" or "rendering" or "performing") == true)
            .Select(ct => ProviderNpi(bundle, ct.Provider))
            .FirstOrDefault(p => p.Npi is not null);
        if (servicing is { Npi: not null } s)
        {
            request.ServicingProviderNPI = s.Npi;
            request.ServicingProviderName = Truncate(s.Name, 300);
        }
    }

    /// <summary>
    /// The NPI of the provider a reference names: from the referenced resource
    /// in the Bundle (a PractitionerRole is followed to its Practitioner, then
    /// its Organization), else from the reference's own identifier when it is
    /// an NPI (or carries no system).
    /// </summary>
    public static (string? Npi, string? Name) ProviderNpi(Bundle bundle, ResourceReference? reference)
    {
        var resource = Resolve(bundle, reference);
        if (resource is PractitionerRole role)
        {
            // One hop only, and only to the two types a role may name, so a
            // reference cycle in the request cannot recurse.
            foreach (var next in new[] { Resolve(bundle, role.Practitioner), Resolve(bundle, role.Organization) })
            {
                if (next is Practitioner or Organization
                    && FromResource(next, reference) is { Npi: not null } found)
                    return found;
            }
        }

        if (FromResource(resource, reference) is { Npi: not null } direct)
            return direct;

        var own = reference?.Identifier;
        if (own is not null && (own.System is null || own.System == NpiSystem)
            && own.Value?.Trim() is { } value && Npi.IsMatch(value))
            return (value, DisplayNameOf(resource) ?? reference?.Display);

        return (null, null);
    }

    private static string? DisplayNameOf(Resource? resource) => resource switch
    {
        Practitioner p => DisplayName(p.Name?.FirstOrDefault()),
        Organization o => o.Name,
        _ => null,
    };

    private static (string? Npi, string? Name) FromResource(Resource? resource, ResourceReference? reference)
    {
        var (identifiers, name) = resource switch
        {
            Practitioner p => (p.Identifier, DisplayName(p.Name?.FirstOrDefault())),
            Organization o => (o.Identifier, o.Name),
            _ => ((List<Identifier>?)null, reference?.Display),
        };

        var npi = identifiers?
            .Where(i => i.System == NpiSystem)
            .Select(i => i.Value?.Trim())
            .FirstOrDefault(v => v is not null && Npi.IsMatch(v));
        return npi is null ? (null, null) : (npi, name);
    }

    private static string? DisplayName(HumanName? name)
    {
        if (name is null) return null;
        var text = string.Join(' ', (name.Given ?? []).Append(name.Family).Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(text) ? name.Text : text;
    }

    // ── Services, diagnoses ───────────────────────────────────────────

    private static void MapServices(
        Bundle bundle, Claim claim, PasAuthorizationRecordRequest request, List<PasMappingError> errors)
    {
        if (claim.Item is null || claim.Item.Count == 0)
        {
            errors.Add(new("Claim.item", "The request must name at least one service (Claim.item)."));
            return;
        }

        foreach (var item in claim.Item)
        {
            var path = $"Claim.item[{item.Sequence}].productOrService";
            var coding = item.ProductOrService?.Coding?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Code));
            if (coding is null)
            {
                errors.Add(new(path, "Each item needs a coded productOrService."));
                continue;
            }

            var code = coding.Code!.Trim();
            if (code.Length > 64 || (coding.System?.Length ?? 0) > 256)
            {
                errors.Add(new(path, "productOrService code (64) or system (256) is too long."));
                continue;
            }

            var units = item.Quantity?.Value ?? 1m;
            if (units <= 0)
            {
                errors.Add(new($"Claim.item[{item.Sequence}].quantity", "Requested quantity must be positive."));
                continue;
            }

            request.RequestedServices.Add(new PasRequestedService
            {
                ProcedureCode = code,
                ProductOrServiceSystem = coding.System,
                ProcedureDescription = Truncate(coding.Display ?? item.ProductOrService?.Text, 500),
                RequestedUnits = units,
                UnitType = new[] { item.Quantity?.Unit, item.Quantity?.Code }
                    .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u) && u.Length <= 10),
                Modifiers = item.Modifier?
                    .SelectMany(m => m.Coding ?? [])
                    .Select(c => c.Code)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!)
                    .ToList() ?? [],
                PlaceOfServiceCode = (item.Location as CodeableConcept)?.Coding?
                    .Select(c => c.Code).FirstOrDefault(c => c is { Length: > 0 and <= 2 }),
                RevenueCode = item.Revenue?.Coding?
                    .Select(c => c.Code).FirstOrDefault(c => c is { Length: > 0 and <= 4 }),
            });
        }

        foreach (var diagnosis in (claim.Diagnosis ?? []).OrderBy(d => d.Sequence ?? int.MaxValue))
        {
            var concept = diagnosis.Diagnosis as CodeableConcept
                          ?? (Resolve(bundle, diagnosis.Diagnosis as ResourceReference) as Condition)?.Code;
            // ICD-10-CM preferred, then ICD-10; other codings (SNOMED, ...) are
            // not what the record's diagnosis codes hold, so a diagnosis with
            // neither is skipped rather than refusing the request.
            var coding = Icd10Coding(concept, Icd10CmSystem) ?? Icd10Coding(concept, Icd10System);
            if (coding is null)
                continue;

            var code = coding.Code!.Trim();
            if (code.Length > 10)
            {
                errors.Add(new($"Claim.diagnosis[{diagnosis.Sequence}]", "Diagnosis codes are limited to 10 characters."));
                continue;
            }

            request.DiagnosisCodes.Add(new PasDiagnosisCode
            {
                Code = code,
                CodeQualifier = request.DiagnosisCodes.Count == 0 ? "BK" : "BF",
                Description = Truncate(coding.Display ?? concept!.Text, 500),
            });
        }
    }

    private static Coding? Icd10Coding(CodeableConcept? concept, string system)
        => concept?.Coding?.FirstOrDefault(c => c.System == system && !string.IsNullOrWhiteSpace(c.Code));

    // ── Priority, dates ───────────────────────────────────────────────

    /// <summary>
    /// X12 UM06 level of service: <c>U</c> (expedited: 72 hours) or <c>E</c>
    /// (standard), or null when the request does not say (the standard clock
    /// applies). The PAS level-of-service extension states UM06 directly, so
    /// it is read first (on the Claim, then on its items); then
    /// <c>Claim.priority</c>. A code is read only in the system it belongs to.
    /// </summary>
    public static string? LevelOfService(Claim claim)
    {
        var extensions = (claim.Extension ?? [])
            .Concat((claim.Item ?? []).SelectMany(i => i.Extension ?? []))
            .Where(e => e.Url == LevelOfServiceExtensionUrl);
        foreach (var extension in extensions)
        {
            if (LevelOfService(extension.Value as CodeableConcept) is { } level)
                return level;
        }

        return LevelOfService(claim.Priority);
    }

    /// <summary>The level of service a concept's codings state, by their systems.</summary>
    public static string? LevelOfService(CodeableConcept? concept)
    {
        foreach (var coding in concept?.Coding ?? [])
        {
            var code = coding.Code?.Trim();
            var level = coding.System switch
            {
                ProcessPrioritySystem => code switch
                {
                    "stat" => "U",
                    "normal" or "deferred" => "E",
                    _ => null,
                },
                X12LevelOfServiceSystem => code switch
                {
                    "U" or "03" => "U",
                    "E" => "E",
                    _ => null,
                },
                _ => null,
            };
            if (level is not null)
                return level;
        }

        return null;
    }

    /// <param name="LatestEnd">The latest date any service names (an open-ended one counts its start).</param>
    private readonly record struct RequestedDates(DateTime From, DateTime? To, bool OpenEnded, DateTime LatestEnd);

    private static RequestedDates? RequestedPeriod(Claim claim)
    {
        DateTime? from = null, to = null;
        var openEnded = false;

        foreach (var item in claim.Item ?? [])
        {
            switch (item.Serviced)
            {
                case Date date when ParseFullDate(date.Value) is { } d:
                    Widen(d, d);
                    break;
                case FhirDateTime dateTime when ParseFullDate(dateTime.Value) is { } d:
                    Widen(d, d);
                    break;
                case Period period when ParseFullDate(period.Start) is { } start:
                    var end = ParseFullDate(period.End);
                    if (end is null) openEnded = true;
                    Widen(start, end ?? start);
                    break;
            }
        }

        if (from is null && ParseFullDate(claim.BillablePeriod?.Start) is { } billableStart)
        {
            var billableEnd = ParseFullDate(claim.BillablePeriod!.End);
            openEnded = billableEnd is null;
            Widen(billableStart, billableEnd ?? billableStart);
        }

        if (from is null)
            return null;

        return new RequestedDates(from.Value, openEnded ? null : to, openEnded, to!.Value);

        void Widen(DateTime start, DateTime end)
        {
            from = from is null || start < from ? start : from;
            to = to is null || end > to ? end : to;
        }
    }

    /// <summary>
    /// The requested period; when a service is open-ended, the later of the
    /// fallback (start + 1 year) and the latest end another service names, so
    /// the fallback never cuts short a service the request bounded later.
    /// </summary>
    private static (DateTime From, DateTime To) ApprovedPeriodFor(RequestedDates requested)
    {
        if (requested.To is { } to)
            return (requested.From, to);

        var fallback = requested.From.Add(OpenEndedApproval);
        return (requested.From, requested.LatestEnd > fallback ? requested.LatestEnd : fallback);
    }

    /// <summary>The date part of a FHIR date/dateTime, only when it names a full day.</summary>
    private static DateTime? ParseFullDate(string? value)
    {
        if (value is null || value.Length < 10)
            return null;

        return DateTime.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : null;
    }

    // ── helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// The Bundle entry a reference names: by <c>Type/id</c> or by
    /// <c>fullUrl</c>. Null for a missing, contained or unresolvable reference.
    /// </summary>
    public static Resource? Resolve(Bundle bundle, ResourceReference? reference)
    {
        var target = reference?.Reference;
        if (string.IsNullOrWhiteSpace(target) || bundle.Entry is null)
            return null;

        foreach (var entry in bundle.Entry)
        {
            var resource = entry.Resource;
            if (resource is null) continue;

            if (string.Equals($"{resource.TypeName}/{resource.Id}", target, StringComparison.Ordinal)
                || string.Equals(entry.FullUrl, target, StringComparison.Ordinal)
                || (entry.FullUrl?.EndsWith("/" + target, StringComparison.Ordinal) ?? false))
                return resource;
        }

        return null;
    }

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}

public sealed record PasMappingError(string Expression, string Message);

public sealed class PasMappingResult
{
    public PasAuthorizationRecordRequest? Request { get; private init; }

    /// <summary>The approved period an approval of this request carries.</summary>
    public DateTime ApprovedFrom { get; private init; }

    public DateTime ApprovedTo { get; private init; }

    public IReadOnlyList<PasMappingError> Errors { get; private init; } = [];

    public bool IsValid => Errors.Count == 0;

    public static PasMappingResult Valid(PasAuthorizationRecordRequest request, (DateTime From, DateTime To) approved)
        => new() { Request = request, ApprovedFrom = approved.From, ApprovedTo = approved.To };

    public static PasMappingResult Invalid(IReadOnlyList<PasMappingError> errors) => new() { Errors = errors };
}

/// <summary>
/// What fhir-service sends to authorization-service's internal
/// automated-decision endpoint (<c>AutomatedDecisionRecordRequest</c> there).
/// </summary>
public sealed class PasAuthorizationRecordRequest
{
    public string AuthorizationNumber { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 (lower-case hex) of this request's payload, set by the
    /// recorder. authorization-service returns the stored record when the same
    /// number is recorded again with the same hash (a retry), and compares it
    /// on fhir-service's compensating read.
    /// </summary>
    public string? RequestHash { get; set; }

    public DateTime ReceivedAt { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string? PatientReference { get; set; }
    public string? CoverageId { get; set; }
    public string PatientFirstName { get; set; } = string.Empty;
    public string PatientLastName { get; set; } = string.Empty;
    public DateTime? PatientDateOfBirth { get; set; }

    /// <summary>authorization-service's <c>LineOfBusiness</c> name; <c>Unknown</c> when undetermined.</summary>
    public string LineOfBusiness { get; set; } = "Unknown";

    [JsonPropertyName("requestingProviderNPI")]
    public string RequestingProviderNPI { get; set; } = string.Empty;
    public string? RequestingProviderName { get; set; }

    [JsonPropertyName("servicingProviderNPI")]
    public string? ServicingProviderNPI { get; set; }
    public string? ServicingProviderName { get; set; }
    public string? ServiceTypeCode { get; set; }
    public string? LevelOfService { get; set; }
    public DateTime? RequestedServiceDateFrom { get; set; }
    public DateTime? RequestedServiceDateTo { get; set; }
    public List<PasDiagnosisCode> DiagnosisCodes { get; set; } = [];
    public List<PasRequestedService> RequestedServices { get; set; } = [];
    public PasAutomatedDecision? Decision { get; set; }
}

public sealed class PasDiagnosisCode
{
    public string Code { get; set; } = string.Empty;
    public string CodeQualifier { get; set; } = "BK";
    public string? Description { get; set; }
}

public sealed class PasRequestedService
{
    public string ProcedureCode { get; set; } = string.Empty;
    public string? ProductOrServiceSystem { get; set; }
    public string? ProcedureDescription { get; set; }
    public List<string> Modifiers { get; set; } = [];
    public decimal RequestedUnits { get; set; } = 1;
    public string? UnitType { get; set; }
    public string? PlaceOfServiceCode { get; set; }
    public string? RevenueCode { get; set; }
}

public sealed class PasAutomatedDecision
{
    /// <summary><c>Approved</c>, <c>Denied</c> or <c>Pended</c>.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public string? PendReason { get; set; }
    public DateTime? ApprovedServiceDateFrom { get; set; }
    public DateTime? ApprovedServiceDateTo { get; set; }
    public DateTime DecidedAt { get; set; }
    public string? Rule { get; set; }

    /// <summary>The line of business the decision was made on; must be the record's.</summary>
    public string LineOfBusiness { get; set; } = "Unknown";

    /// <summary>The service state the state-specific rules used, or null when none ran on a state.</summary>
    public string? StateCode { get; set; }
}
