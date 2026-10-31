using CloudHealthOffice.Infrastructure.Import.Cpcds;

namespace AuthorizationService.Import;

/// <summary>
/// The columns of the authorization CSV a plan's UM system exports for CHO.
/// One row per requested service line; the header columns repeat on each row
/// of the same authorization (only the first row's values are used). A file
/// with no service-line columns is one row per authorization.
/// </summary>
public enum AuthorizationCsvColumn
{
    AuthorizationNumber,
    MemberId,
    Status,
    ReceivedDate,
    DecisionDate,
    RequestingProviderNpi,
    RequestingProviderName,
    ServicingProviderNpi,
    ServicingProviderName,
    FacilityNpi,
    FacilityName,
    RequestType,
    ServiceTypeCode,
    LevelOfService,
    LineOfBusiness,
    RequestedFromDate,
    RequestedToDate,
    ApprovedFromDate,
    ApprovedToDate,
    ExpirationDate,
    DiagnosisCodes,
    DenialReasonCode,
    DenialReason,
    PatientFirstName,
    PatientLastName,
    PatientBirthDate,
    LineNumber,
    ProcedureCode,
    CodeSystem,
    ProcedureDescription,
    Modifiers,
    RequestedUnits,
    ApprovedUnits,
    UnitType,
    PlaceOfService,
    RevenueCode,
    LineStatus,
    LineDenialReasonCode,
    LineDenialReason,
}

public static class AuthorizationCsvColumns
{
    /// <summary>The columns every file must have.</summary>
    public static readonly IReadOnlyList<AuthorizationCsvColumn> Required =
    [
        AuthorizationCsvColumn.AuthorizationNumber,
        AuthorizationCsvColumn.MemberId,
        AuthorizationCsvColumn.Status,
        AuthorizationCsvColumn.ReceivedDate,
        AuthorizationCsvColumn.RequestingProviderNpi,
    ];

    /// <summary>The documented header for each column, then the other names UM exports commonly use.</summary>
    private static readonly Dictionary<AuthorizationCsvColumn, string[]> Names = new()
    {
        [AuthorizationCsvColumn.AuthorizationNumber] = ["authorization_number", "auth_number", "authorization_id", "auth_id", "prior_auth_number", "certification_number"],
        [AuthorizationCsvColumn.MemberId] = ["member_id", "subscriber_id", "patient_id"],
        [AuthorizationCsvColumn.Status] = ["status", "decision", "authorization_status", "auth_status"],
        [AuthorizationCsvColumn.ReceivedDate] = ["received_date", "request_date", "submitted_date", "received_at"],
        [AuthorizationCsvColumn.DecisionDate] = ["decision_date", "decided_date", "determination_date", "decided_at"],
        [AuthorizationCsvColumn.RequestingProviderNpi] = ["requesting_provider_npi", "requesting_npi", "requester_npi"],
        [AuthorizationCsvColumn.RequestingProviderName] = ["requesting_provider_name", "requesting_name", "requester_name"],
        [AuthorizationCsvColumn.ServicingProviderNpi] = ["servicing_provider_npi", "servicing_npi", "rendering_npi"],
        [AuthorizationCsvColumn.ServicingProviderName] = ["servicing_provider_name", "servicing_name", "rendering_name"],
        [AuthorizationCsvColumn.FacilityNpi] = ["facility_npi"],
        [AuthorizationCsvColumn.FacilityName] = ["facility_name"],
        [AuthorizationCsvColumn.RequestType] = ["request_type", "authorization_type", "review_type"],
        [AuthorizationCsvColumn.ServiceTypeCode] = ["service_type_code", "service_type"],
        [AuthorizationCsvColumn.LevelOfService] = ["level_of_service", "urgency", "priority"],
        [AuthorizationCsvColumn.LineOfBusiness] = ["line_of_business", "lob"],
        [AuthorizationCsvColumn.RequestedFromDate] = ["requested_from_date", "service_from_date", "requested_start_date"],
        [AuthorizationCsvColumn.RequestedToDate] = ["requested_to_date", "service_to_date", "requested_end_date"],
        [AuthorizationCsvColumn.ApprovedFromDate] = ["approved_from_date", "approved_start_date", "effective_date"],
        [AuthorizationCsvColumn.ApprovedToDate] = ["approved_to_date", "approved_end_date"],
        [AuthorizationCsvColumn.ExpirationDate] = ["expiration_date", "expiry_date"],
        [AuthorizationCsvColumn.DiagnosisCodes] = ["diagnosis_codes", "diagnosis_code", "icd10_codes"],
        [AuthorizationCsvColumn.DenialReasonCode] = ["denial_reason_code"],
        [AuthorizationCsvColumn.DenialReason] = ["denial_reason"],
        [AuthorizationCsvColumn.PatientFirstName] = ["patient_first_name", "member_first_name"],
        [AuthorizationCsvColumn.PatientLastName] = ["patient_last_name", "member_last_name"],
        [AuthorizationCsvColumn.PatientBirthDate] = ["patient_birth_date", "member_birth_date", "patient_dob", "member_dob", "date_of_birth"],
        [AuthorizationCsvColumn.LineNumber] = ["line_number", "service_line_number"],
        [AuthorizationCsvColumn.ProcedureCode] = ["procedure_code", "service_code", "cpt_hcpcs_code"],
        [AuthorizationCsvColumn.CodeSystem] = ["code_system", "procedure_code_system"],
        [AuthorizationCsvColumn.ProcedureDescription] = ["procedure_description", "service_description"],
        [AuthorizationCsvColumn.Modifiers] = ["modifiers", "procedure_modifiers"],
        [AuthorizationCsvColumn.RequestedUnits] = ["requested_units", "requested_quantity"],
        [AuthorizationCsvColumn.ApprovedUnits] = ["approved_units", "approved_quantity"],
        [AuthorizationCsvColumn.UnitType] = ["unit_type"],
        [AuthorizationCsvColumn.PlaceOfService] = ["place_of_service", "place_of_service_code"],
        [AuthorizationCsvColumn.RevenueCode] = ["revenue_code"],
        [AuthorizationCsvColumn.LineStatus] = ["line_status", "line_decision"],
        [AuthorizationCsvColumn.LineDenialReasonCode] = ["line_denial_reason_code"],
        [AuthorizationCsvColumn.LineDenialReason] = ["line_denial_reason"],
    };

    private static readonly Dictionary<string, AuthorizationCsvColumn> ByKey = BuildKeys();

    private static Dictionary<string, AuthorizationCsvColumn> BuildKeys()
    {
        var keys = new Dictionary<string, AuthorizationCsvColumn>(StringComparer.Ordinal);
        foreach (var (column, names) in Names)
        {
            keys[CpcdsHeaders.Normalize(column.ToString())] = column;
            foreach (var name in names)
                keys[CpcdsHeaders.Normalize(name)] = column;
        }
        return keys;
    }

    /// <summary>The header a file should use for <paramref name="column"/>.</summary>
    public static string DisplayName(AuthorizationCsvColumn column) => Names[column][0];

    /// <summary>Every column with its documented header, in order.</summary>
    public static IEnumerable<(AuthorizationCsvColumn Column, string Header)> All
        => Enum.GetValues<AuthorizationCsvColumn>().Select(c => (c, DisplayName(c)));

    /// <summary>
    /// Where each known column sits in <paramref name="header"/>, and the
    /// headers no column claimed. <paramref name="columnMap"/> maps a column
    /// (its documented header or enum name) to the file's own header, for
    /// files that name a column some other way.
    /// </summary>
    public static (Dictionary<AuthorizationCsvColumn, int> Positions, List<string> Unrecognized) Resolve(
        IReadOnlyList<string> header, IReadOnlyDictionary<string, string>? columnMap)
    {
        var mapped = new Dictionary<string, AuthorizationCsvColumn>(StringComparer.Ordinal);
        foreach (var (column, fileHeader) in columnMap ?? new Dictionary<string, string>())
        {
            if (ByKey.TryGetValue(CpcdsHeaders.Normalize(column), out var known) && !string.IsNullOrWhiteSpace(fileHeader))
                mapped[CpcdsHeaders.Normalize(fileHeader)] = known;
        }

        var positions = new Dictionary<AuthorizationCsvColumn, int>();
        var unrecognized = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            var key = CpcdsHeaders.Normalize(header[i]);
            if (key.Length == 0)
                continue;
            if ((mapped.TryGetValue(key, out var column) || ByKey.TryGetValue(key, out column))
                && !positions.ContainsKey(column))
            {
                positions[column] = i;
            }
            else
            {
                unrecognized.Add(header[i].Trim());
            }
        }
        return (positions, unrecognized);
    }
}
