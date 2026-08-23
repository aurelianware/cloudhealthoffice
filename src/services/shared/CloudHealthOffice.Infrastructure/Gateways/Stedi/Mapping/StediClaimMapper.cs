using System.Globalization;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Maps canonical <see cref="GatewayClaimSubmissionRequest"/> onto Stedi's
/// documented 837 JSON contracts and normalizes the synchronous submission
/// response. Does not interpret 277CA, adjudication, or payment.
/// </summary>
internal static class StediClaimMapper
{
    public static StediClaimSubmissionRequestDto ToStediRequest(
        GatewayClaimSubmissionRequest request, string tradingPartnerServiceId, string usageIndicator)
    {
        var billing = request.BillingProvider ?? new GatewayClaimProvider();
        var subscriber = request.Subscriber ?? new GatewayEligibilityPerson();
        var dto = new StediClaimSubmissionRequestDto
        {
            UsageIndicator = usageIndicator,
            TradingPartnerServiceId = tradingPartnerServiceId,
            TradingPartnerName = NullIfBlank(request.PayerName),
            Submitter = new StediClaimSubmitterDto
            {
                OrganizationName = NullIfBlank(billing.OrganizationName) ?? "Cloud Health Office",
                SubmitterIdentification = NullIfBlank(billing.Npi) ?? request.TenantId,
                ContactInformation = string.IsNullOrWhiteSpace(billing.Phone)
                    ? null
                    : new StediClaimContactDto { Name = billing.OrganizationName, PhoneNumber = billing.Phone }
            },
            Receiver = string.IsNullOrWhiteSpace(request.PayerName)
                ? null
                : new StediClaimReceiverDto { OrganizationName = request.PayerName },
            Billing = new StediClaimBillingDto
            {
                Npi = billing.Npi,
                EmployerId = NullIfBlank(billing.EmployerId),
                TaxonomyCode = NullIfBlank(billing.TaxonomyCode),
                OrganizationName = NullIfBlank(billing.OrganizationName),
                Address = MapAddress(billing),
                ContactInformation = string.IsNullOrWhiteSpace(billing.Phone)
                    ? null
                    : new StediClaimContactDto { Name = billing.OrganizationName, PhoneNumber = billing.Phone }
            },
            Subscriber = new StediClaimSubscriberDto
            {
                MemberId = subscriber.MemberId,
                FirstName = subscriber.FirstName,
                LastName = subscriber.LastName,
                DateOfBirth = FormatDate(subscriber.DateOfBirth),
                GroupNumber = NullIfBlank(request.GroupNumber)
            },
            Rendering = MapRendering(request.RenderingProvider),
            Dependent = MapDependent(request),
            ClaimInformation = MapClaimInformation(request)
        };

        return dto;
    }

    public static GatewayClaimSubmissionResult ToCanonical(
        GatewayClaimSubmissionRequest request,
        StediClaimSubmissionResponseDto response,
        string transmissionId,
        string idempotencyKey,
        bool replay)
    {
        var accepted = string.Equals(response.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
        var errors = (response.Errors ?? new List<StediClaimErrorDto>())
            .Select(e => string.IsNullOrWhiteSpace(e.Description) ? e.Code : e.Description)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();

        return new GatewayClaimSubmissionResult
        {
            ClaimId = request.ClaimId,
            ClaimVersion = request.ClaimVersion,
            ClaimType = request.ClaimType,
            TransmissionStatus = accepted
                ? GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
                : GatewayClaimTransmissionStatus.SubmissionRejectedByGateway,
            TransmissionId = transmissionId,
            SubmissionId = response.ClaimReference?.SubmissionId ?? response.ControlNumber,
            ExternalTransactionId = response.Meta?.TraceId ?? response.ClaimReference?.SubmissionId,
            IdempotencyKey = idempotencyKey,
            AcceptedForProcessing = accepted,
            ReplayOfExistingTransmission = replay,
            Errors = errors
        };
    }

    private static StediClaimInformationDto MapClaimInformation(GatewayClaimSubmissionRequest request)
    {
        var info = new StediClaimInformationDto
        {
            PatientControlNumber = Truncate(request.ClaimId, 20),
            ClaimChargeAmount = FormatMoney(request.TotalCharge),
            PlaceOfServiceCode = NullIfBlank(request.PlaceOfServiceCode),
            ClaimFrequencyCode = string.IsNullOrWhiteSpace(request.FrequencyCode) ? "1" : request.FrequencyCode,
            ClaimSupplementalInformation = MapSupplemental(request),
            HealthCareCodeInformation = request.ClaimType == GatewayClaimType.Institutional
                ? null
                : request.Diagnoses.Select(d => new StediDiagnosisDto
                {
                    DiagnosisTypeCode = string.IsNullOrWhiteSpace(d.Qualifier) ? "ABK" : d.Qualifier,
                    DiagnosisCode = d.Code
                }).ToList(),
            PrincipalDiagnosis = request.ClaimType == GatewayClaimType.Institutional
                ? MapPrincipal(request)
                : null,
            ClaimDateInformation = request.ClaimType == GatewayClaimType.Institutional
                ? new StediClaimDateInformationDto
                {
                    StatementBeginDate = FormatDate(request.ServiceDateFrom),
                    StatementEndDate = FormatDate(request.ServiceDateTo ?? request.ServiceDateFrom),
                    AdmissionDateAndHour = request.AdmissionDate is { } adm
                        ? adm.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "0000"
                        : null
                }
                : null,
            ServiceLines = request.ServiceLines.Select(l => MapLine(request.ClaimType, l)).ToList()
        };

        return info;
    }

    private static StediClaimServiceLineDto MapLine(GatewayClaimType type, GatewayClaimLine line)
    {
        var dto = new StediClaimServiceLineDto
        {
            AssignedNumber = line.LineNumber.ToString(CultureInfo.InvariantCulture),
            ServiceDate = FormatDate(line.ServiceDateFrom),
            ProviderControlNumber = line.LineItemControlNumber()
        };

        var modifiers = line.Modifiers.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        var pointers = line.DiagnosisPointers.Count == 0
            ? null
            : new StediDiagnosisPointersDto
            {
                DiagnosisCodePointers = line.DiagnosisPointers
                    .Select(p => p.ToString(CultureInfo.InvariantCulture))
                    .ToList()
            };

        switch (type)
        {
            case GatewayClaimType.Institutional:
                dto.InstitutionalService = new StediInstitutionalServiceDto
                {
                    ProcedureCode = line.ProcedureCode,
                    ServiceLineRevenueCode = NullIfBlank(line.RevenueCode),
                    LineItemChargeAmount = FormatMoney(line.ChargeAmount),
                    ServiceUnitCount = FormatDecimal(line.Units)
                };
                break;
            case GatewayClaimType.Dental:
                dto.DentalService = new StediDentalServiceDto
                {
                    ProcedureCode = line.ProcedureCode,
                    ProcedureModifiers = modifiers.Count == 0 ? null : modifiers,
                    LineItemChargeAmount = FormatMoney(line.ChargeAmount),
                    ToothCode = NullIfBlank(line.ToothNumber),
                    ToothSurface = NullIfBlank(line.ToothSurface),
                    OralCavityDesignation = string.IsNullOrWhiteSpace(line.OralCavity)
                        ? null
                        : new List<string> { line.OralCavity }
                };
                break;
            default:
                dto.ProfessionalService = new StediProfessionalServiceDto
                {
                    ProcedureCode = line.ProcedureCode,
                    ProcedureModifiers = modifiers.Count == 0 ? null : modifiers,
                    LineItemChargeAmount = FormatMoney(line.ChargeAmount),
                    ServiceUnitCount = FormatDecimal(line.Units),
                    CompositeDiagnosisCodePointers = pointers
                };
                break;
        }

        return dto;
    }

    private static StediPrincipalDiagnosisDto? MapPrincipal(GatewayClaimSubmissionRequest request)
    {
        var principal = request.Diagnoses.FirstOrDefault(d =>
            string.Equals(d.Qualifier, "ABK", StringComparison.OrdinalIgnoreCase))
            ?? request.Diagnoses.FirstOrDefault();
        if (principal is null || string.IsNullOrWhiteSpace(principal.Code))
        {
            return null;
        }

        return new StediPrincipalDiagnosisDto { PrincipalDiagnosisCode = principal.Code };
    }

    private static StediClaimSupplementalDto? MapSupplemental(GatewayClaimSubmissionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PriorAuthorizationNumber) &&
            string.IsNullOrWhiteSpace(request.ReferralNumber))
        {
            return null;
        }

        return new StediClaimSupplementalDto
        {
            PriorAuthorizationNumber = NullIfBlank(request.PriorAuthorizationNumber),
            ReferralNumber = NullIfBlank(request.ReferralNumber)
        };
    }

    private static StediClaimDependentDto? MapDependent(GatewayClaimSubmissionRequest request)
    {
        if (request.Patient is null || !request.Patient.HasIdentity || request.Patient.IsSelf)
        {
            return null;
        }

        return new StediClaimDependentDto
        {
            FirstName = request.Patient.FirstName,
            LastName = request.Patient.LastName,
            DateOfBirth = FormatDate(request.Patient.DateOfBirth),
            RelationshipToSubscriberCode = MapRelationship(request.Patient.RelationshipToSubscriber)
        };
    }

    private static StediClaimRenderingDto? MapRendering(GatewayClaimProvider? provider)
    {
        if (provider is null || !provider.HasNpi)
        {
            return null;
        }

        return new StediClaimRenderingDto
        {
            Npi = provider.Npi,
            OrganizationName = NullIfBlank(provider.OrganizationName),
            LastName = NullIfBlank(provider.LastName),
            FirstName = NullIfBlank(provider.FirstName)
        };
    }

    private static StediClaimAddressDto? MapAddress(GatewayClaimProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Address1) &&
            string.IsNullOrWhiteSpace(provider.City))
        {
            return null;
        }

        return new StediClaimAddressDto
        {
            Address1 = provider.Address1,
            Address2 = NullIfBlank(provider.Address2),
            City = provider.City,
            State = provider.State,
            PostalCode = provider.PostalCode
        };
    }

    private static string MapRelationship(string? relationship) => relationship switch
    {
        GatewayEligibilityPerson.Relationship.Spouse => "01",
        GatewayEligibilityPerson.Relationship.Child => "19",
        GatewayEligibilityPerson.Relationship.Self => "18",
        _ => "G8"
    };

    private static string FormatMoney(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateOnly? date) =>
        date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
