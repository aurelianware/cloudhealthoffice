using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Maps provider-service responses onto the portal's provider view models.
///
/// <para>
/// provider-service returns its stored <c>Provider</c> record from
/// <c>GET /providers/list</c>, <c>GET /providers/search</c> and
/// <c>GET /providers/{id}</c>: <c>fullName</c>, <c>firstName</c>,
/// <c>providerType</c>, <c>primarySpecialty</c>, <c>organizationName</c>,
/// <c>networkParticipations</c> and a <c>credentials</c> string such as "MD".
/// The portal grid and record page bind to a flatter shape (<c>name</c>,
/// <c>practiceType</c>, <c>specialty</c>, <c>networkStatus</c>, ...). Reading
/// the stored record straight into that shape left every one of those
/// columns blank, and the <c>credentials</c> string made the detail read
/// fail outright.
/// </para>
///
/// <para>
/// A payload that already carries <c>name</c> is treated as the flat
/// shape and deserialized as before, so a service that projects for the
/// portal keeps working.
/// </para>
/// </summary>
public static class ProviderRecordProjection
{
    public const string InNetwork = "In-Network";
    public const string OutOfNetwork = "Out-of-Network";
    public const string Pending = "Pending";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static List<ProviderListItem> ToListItems(JsonElement payload, DateTime asOf)
        => Items(payload).Select(item => ToListItem(item, asOf)).ToList();

    public static List<ProviderSummary> ToSummaries(JsonElement payload, DateTime asOf)
        => Items(payload).Select(item =>
        {
            if (IsFlatShape(item))
            {
                return item.Deserialize<ProviderSummary>(WebJson) ?? new ProviderSummary();
            }

            var listItem = ToListItem(item, asOf);
            return new ProviderSummary
            {
                ProviderId = listItem.ProviderId,
                NPI = listItem.NPI,
                Name = listItem.Name,
                Specialty = listItem.Specialty,
                City = listItem.City,
                State = listItem.State,
            };
        }).ToList();

    public static ProviderListItem ToListItem(JsonElement item, DateTime asOf)
    {
        if (IsFlatShape(item))
        {
            return item.Deserialize<ProviderListItem>(WebJson) ?? new ProviderListItem();
        }

        var target = new ProviderListItem();
        Fill(target, item, asOf);
        return target;
    }

    public static ProviderDetails? ToDetails(JsonElement item, DateTime asOf)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (IsFlatShape(item))
        {
            return item.Deserialize<ProviderDetails>(WebJson);
        }

        var details = new ProviderDetails();
        Fill(details, item, asOf);
        details.TaxonomyCode = Str(item, "taxonomyCode");
        details.LastVerifiedAt = DateOffset(item, "lastVerifiedAt");
        details.NextVerificationDue = DateOffset(item, "nextVerificationDue");

        if (item.TryGetProperty("boardCertifications", out var boards) && boards.ValueKind == JsonValueKind.Array)
        {
            foreach (var board in boards.EnumerateArray())
            {
                var label = board.ValueKind == JsonValueKind.String
                    ? board.GetString()
                    : FirstNonEmpty(Str(board, "specialty"), Str(board, "board"));
                if (!string.IsNullOrWhiteSpace(label))
                {
                    details.BoardCertifications.Add(label);
                }
            }
        }

        var address = Str(item, "address");
        if (!string.IsNullOrWhiteSpace(address) || !string.IsNullOrWhiteSpace(details.City))
        {
            details.Locations.Add(new PracticeLocation
            {
                LocationId = "primary",
                Name = string.IsNullOrWhiteSpace(details.PracticeName) ? "Primary practice location" : details.PracticeName,
                AddressLine1 = address,
                City = details.City,
                State = details.State,
                ZipCode = Str(item, "zipCode"),
                Phone = Str(item, "phone"),
                Fax = NullIfEmpty(Str(item, "fax")),
                IsPrimary = true,
            });
        }

        foreach (var participation in Participations(item))
        {
            var effective = Date(participation, "effectiveDate") ?? DateTime.MinValue;
            var termination = Date(participation, "terminationDate");
            var networkId = Str(participation, "networkId");
            details.NetworkAssignments.Add(new NetworkAssignment
            {
                NetworkId = networkId,
                NetworkName = string.IsNullOrWhiteSpace(networkId) ? Str(participation, "networkTier") : networkId,
                PlanName = Str(participation, "planId"),
                EffectiveDate = effective,
                TerminationDate = termination,
                Status = !IsActive(participation, asOf)
                    ? (termination is { } t && t.Date < asOf.Date ? "Terminated" : "Future")
                    : IsOutOfNetworkTier(participation) ? OutOfNetwork : "Active",
                LineOfBusiness = Str(participation, "lineOfBusiness"),
                PanelLimit = Int(participation, "panelLimit"),
                PanelAccepted = Bool(participation, "panelAccepted"),
                AcceptedLobs = StrList(participation, "acceptedLobs"),
                MinAcceptedAgeYears = Int(participation, "minAcceptedAgeYears"),
                MaxAcceptedAgeYears = Int(participation, "maxAcceptedAgeYears"),
            });
        }

        return details;
    }

    private static void Fill(ProviderListItem target, JsonElement item, DateTime asOf)
    {
        var isOrganization = string.Equals(Str(item, "providerType"), "Organization", StringComparison.OrdinalIgnoreCase);
        var organizationName = FirstNonEmpty(Str(item, "organizationName"), Str(item, "dbaName"));

        target.ProviderId = FirstNonEmpty(Str(item, "providerId"), Str(item, "id"));
        target.NPI = Str(item, "npi");
        target.Name = FirstNonEmpty(
            Str(item, "fullName"),
            isOrganization ? organizationName : ComposeName(item),
            organizationName,
            target.NPI);
        target.PracticeType = isOrganization ? "Organization" : "Individual";
        target.Specialty = SpecialtyLabel(FirstNonEmpty(Str(item, "primarySpecialty"), Str(item, "taxonomyCode")));
        target.PracticeName = organizationName;
        target.City = Str(item, "city");
        target.State = Str(item, "state");
        target.CredentialingStatus = Str(item, "credentialingStatus");
        target.IntegrityScore = Int(item, "integrityScore");
        target.IntegrityRating = NullIfEmpty(Str(item, "integrityRating"));

        var active = Participations(item).Where(p => IsActive(p, asOf)).ToList();
        target.NetworkStatus = active.Any(p => !IsOutOfNetworkTier(p))
            ? InNetwork
            : Participations(item).Any(p => !IsOutOfNetworkTier(p) && Date(p, "effectiveDate") is { } start && start.Date > asOf.Date)
                ? Pending
                : OutOfNetwork;
        target.NetworkCount = active
            .Where(p => !IsOutOfNetworkTier(p))
            .Select(p => FirstNonEmpty(Str(p, "networkId"), Str(p, "planId"), Str(p, "networkTier")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    // provider-service documents primarySpecialty as a NUCC taxonomy code,
    // and seeded data (MCC, CPCDS imports) often stores the code itself.
    // Show a readable label for the common codes; anything else passes through.
    private static readonly Dictionary<string, string> TaxonomyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["207Q00000X"] = "Family Medicine",
        ["207R00000X"] = "Internal Medicine",
        ["208D00000X"] = "General Practice",
        ["207V00000X"] = "Obstetrics & Gynecology",
        ["208600000X"] = "Surgery",
        ["2084N0400X"] = "Neurology",
        ["207RC0000X"] = "Cardiology",
        ["2085R0202X"] = "Diagnostic Radiology",
        ["207Y00000X"] = "Otolaryngology",
        ["2083P0901X"] = "Preventive Medicine",
        ["208000000X"] = "Pediatrics",
        ["2084P0800X"] = "Psychiatry",
        ["207N00000X"] = "Dermatology",
        ["207X00000X"] = "Orthopaedic Surgery",
        ["207P00000X"] = "Emergency Medicine",
        ["207L00000X"] = "Anesthesiology",
        ["207W00000X"] = "Ophthalmology",
        ["363L00000X"] = "Nurse Practitioner",
        ["363A00000X"] = "Physician Assistant",
        ["1223G0001X"] = "General Dentistry",
        ["122300000X"] = "Dentist",
        ["282N00000X"] = "General Acute Care Hospital",
        ["261QP2300X"] = "Primary Care Clinic",
        ["261QF0400X"] = "FQHC",
        ["261QR1300X"] = "Rural Health Clinic",
        ["261QU0200X"] = "Urgent Care",
        ["314000000X"] = "Skilled Nursing Facility",
        ["283Q00000X"] = "Psychiatric Hospital",
        ["101YM0800X"] = "Mental Health Counselor",
        ["193200000X"] = "Multi-Specialty Group",
    };

    private static string SpecialtyLabel(string value)
        => TaxonomyLabels.TryGetValue(value, out var label) ? label : value;

    /// <summary>
    /// True when the payload is already in the portal's flat shape.
    /// The stored provider record never carries a <c>name</c> property.
    /// </summary>
    private static bool IsFlatShape(JsonElement item)
        => item.ValueKind == JsonValueKind.Object
           && item.TryGetProperty("name", out var name)
           && name.ValueKind == JsonValueKind.String;

    private static IEnumerable<JsonElement> Items(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        }

        // Tolerate a paged envelope ({ "providers": [...] } or { "items": [...] }).
        if (payload.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "providers", "items", "results" })
            {
                if (payload.TryGetProperty(key, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    return inner.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
                }
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static IEnumerable<JsonElement> Participations(JsonElement item)
        => item.TryGetProperty("networkParticipations", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : Enumerable.Empty<JsonElement>();

    private static bool IsActive(JsonElement participation, DateTime asOf)
    {
        var effective = Date(participation, "effectiveDate");
        var termination = Date(participation, "terminationDate");
        return (effective is null || effective.Value.Date <= asOf.Date)
               && (termination is null || termination.Value.Date >= asOf.Date);
    }

    private static bool IsOutOfNetworkTier(JsonElement participation)
    {
        var tier = Str(participation, "networkTier").Replace("-", string.Empty).Replace(" ", string.Empty);
        return tier.Equals("OutOfNetwork", StringComparison.OrdinalIgnoreCase)
               || tier.Equals("OON", StringComparison.OrdinalIgnoreCase);
    }

    private static string ComposeName(JsonElement item)
        => string.Join(' ', new[]
            {
                Str(item, "firstName"), Str(item, "middleName"), Str(item, "lastName"),
            }
            .Where(part => !string.IsNullOrWhiteSpace(part)))
            + (string.IsNullOrWhiteSpace(Str(item, "credentials")) ? string.Empty : $", {Str(item, "credentials")}");

    private static string Str(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static List<string> StrList(JsonElement item, string property)
        => item.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList()
            : new List<string>();

    private static int? Int(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
            ? n
            : null;

    private static bool? Bool(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTime? Date(JsonElement item, string property)
        => DateTime.TryParse(Str(item, property), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static DateTimeOffset? DateOffset(JsonElement item, string property)
        => DateTimeOffset.TryParse(Str(item, property), out var d) ? d : null;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
