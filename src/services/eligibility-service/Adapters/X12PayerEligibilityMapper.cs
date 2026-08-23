using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders.Models;
using EligibilityService.Models;
using EligibilityService.Services;

namespace EligibilityService.Adapters;

/// <summary>
/// Maps existing eligibility-service X12 270/271 models onto the canonical
/// payer-side inquiry / response. X12 AAA codes live here, never in the
/// Cloud Health Office responder.
/// </summary>
public static class X12PayerEligibilityMapper
{
    public static PayerEligibilityInquiry ToInquiry(Edi270ParseResult parsed)
    {
        var edi = parsed.Inquiry;
        var subscriber = new GatewayEligibilityPerson
        {
            MemberId = NullIfBlank(edi.SubscriberId),
            FirstName = NullIfBlank(edi.SubscriberFirstName),
            LastName = NullIfBlank(edi.SubscriberLastName),
            DateOfBirth = ToDateOnly(edi.SubscriberDOB),
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
        };

        GatewayEligibilityPerson? patient = null;
        if (!string.IsNullOrWhiteSpace(edi.DependentFirstName) ||
            !string.IsNullOrWhiteSpace(edi.DependentLastName) ||
            edi.DependentDOB.HasValue)
        {
            patient = new GatewayEligibilityPerson
            {
                FirstName = NullIfBlank(edi.DependentFirstName),
                LastName = NullIfBlank(edi.DependentLastName),
                DateOfBirth = ToDateOnly(edi.DependentDOB),
                RelationshipToSubscriber = MapRelationship(edi.DependentRelationship)
            };
        }

        var serviceDate = edi.ServiceDateFrom.HasValue
            ? DateOnly.FromDateTime(DateTime.SpecifyKind(edi.ServiceDateFrom.Value, DateTimeKind.Unspecified))
            : DateOnly.FromDateTime(DateTime.UtcNow);

        return new PayerEligibilityInquiry
        {
            TransactionId = NullIfBlank(edi.ControlNumber) ?? edi.Id,
            ExternalTransactionId = NullIfBlank(edi.ControlNumber),
            PayerId = NullIfBlank(edi.PayerId),
            PayerName = NullIfBlank(edi.PayerName),
            TradingPartnerId = NullIfBlank(parsed.InterchangeReceiverId),
            AdapterName = "x12",
            RequestingProvider = new PayerEligibilityProvider
            {
                Npi = NullIfBlank(edi.ProviderNPI) ?? NullIfBlank(edi.ProviderId),
            },
            Subscriber = subscriber,
            Patient = patient,
            ServiceTypeCodes = new List<string>
            {
                string.IsNullOrWhiteSpace(edi.ServiceTypeCode)
                    ? ServiceTypeCode.HealthBenefitPlanCoverage
                    : edi.ServiceTypeCode
            },
            DateOfService = serviceDate,
            SourceMetadata = new PayerEligibilitySourceMetadata
            {
                Network = "x12",
                InterchangeControlNumber = NullIfBlank(edi.ControlNumber)
            }
        };
    }

    public static EligibilityResponse ToServiceResponse(
        EligibilityInquiry inquiry, PayerEligibilityResponse canonical)
    {
        var covered = canonical.IsEligible;
        return new EligibilityResponse
        {
            Id = canonical.ChoTransactionId,
            InquiryId = inquiry.Id,
            TenantId = canonical.TenantId ?? string.Empty,
            ResponseCode = covered ? "Y" : "N",
            StatusCode = canonical.CoverageStatus == PayerEligibilityCoverageStatus.Active ? "1" : "6",
            RejectionReason = canonical.RejectionMessage,
            IsCovered = covered,
            CoverageLevel = "IND",
            InsurancePlanName = canonical.PlanName ?? string.Empty,
            GroupNumber = canonical.GroupNumber ?? string.Empty,
            CoverageBeginDate = ToDateTime(canonical.CoverageEffectiveDate),
            CoverageEndDate = ToDateTime(canonical.CoverageTerminationDate),
            Benefits = canonical.Benefits.Select(MapBenefit).ToList(),
            Deductible = MapDeductible(canonical.Deductible),
            OutOfPocket = MapOutOfPocket(canonical.OutOfPocket),
            ControlNumber = inquiry.ControlNumber,
            ResponseDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow
        };
    }

    /// <summary>
    /// X12 005010X279A1 AAA03 mapping. Transport adapters own this table;
    /// the responder returns <see cref="EligibilityBusinessStatus"/> only.
    /// </summary>
    public static string ToAaaCode(EligibilityBusinessStatus status) =>
        status switch
        {
            EligibilityBusinessStatus.Success => string.Empty,
            EligibilityBusinessStatus.InvalidRequest => "15",
            EligibilityBusinessStatus.InvalidSubscriber => "72",
            EligibilityBusinessStatus.SubscriberNotFound => "75",
            EligibilityBusinessStatus.SubscriberAmbiguous => "76",
            EligibilityBusinessStatus.DependentNotFound => "67",
            EligibilityBusinessStatus.InvalidDependent => "65",
            EligibilityBusinessStatus.InvalidProvider => "43",
            EligibilityBusinessStatus.InvalidPayer => "79",
            EligibilityBusinessStatus.AmbiguousPayer => "79",
            EligibilityBusinessStatus.UnsupportedServiceType => "42",
            EligibilityBusinessStatus.InvalidDate => "57",
            EligibilityBusinessStatus.UnableToRespond => "42",
            _ => "42"
        };

    private static EligibilityBenefit MapBenefit(GatewayEligibilityBenefit benefit) =>
        new()
        {
            ServiceTypeCode = benefit.ServiceTypeCode,
            ServiceTypeName = benefit.ServiceTypeName,
            CoverageLevel = benefit.CoverageLevel ?? string.Empty,
            MonetaryAmount = benefit.Amount ?? benefit.CopayAmount,
            Percentage = benefit.Percent ?? benefit.CoinsurancePercent,
            NetworkIndicator = benefit.InNetwork ? "Y" : "N",
            AuthorizationRequired = benefit.AuthorizationRequired == true ? "Y" : "N",
            TimePeriodQualifier = benefit.TimePeriod ?? string.Empty
        };

    private static DeductibleInfo? MapDeductible(PayerEligibilityCostShare? costShare)
    {
        if (costShare is null) return null;
        return new DeductibleInfo
        {
            IndividualDeductible = costShare.IndividualAmount ?? 0,
            IndividualDeductibleMet = costShare.IndividualMet ?? 0,
            IndividualDeductibleRemaining = costShare.IndividualRemaining ?? 0,
            FamilyDeductible = costShare.FamilyAmount ?? 0,
            FamilyDeductibleMet = costShare.FamilyMet ?? 0,
            FamilyDeductibleRemaining = costShare.FamilyRemaining ?? 0,
            TimePeriod = costShare.TimePeriod
        };
    }

    private static OutOfPocketInfo? MapOutOfPocket(PayerEligibilityCostShare? costShare)
    {
        if (costShare is null) return null;
        return new OutOfPocketInfo
        {
            IndividualOOPMax = costShare.IndividualAmount ?? 0,
            IndividualOOPMet = costShare.IndividualMet ?? 0,
            IndividualOOPRemaining = costShare.IndividualRemaining ?? 0,
            FamilyOOPMax = costShare.FamilyAmount ?? 0,
            FamilyOOPMet = costShare.FamilyMet ?? 0,
            FamilyOOPRemaining = costShare.FamilyRemaining ?? 0,
            TimePeriod = costShare.TimePeriod
        };
    }

    private static string MapRelationship(string? x12) =>
        x12 switch
        {
            "18" => GatewayEligibilityPerson.Relationship.Self,
            "01" => GatewayEligibilityPerson.Relationship.Spouse,
            "19" => GatewayEligibilityPerson.Relationship.Child,
            _ => GatewayEligibilityPerson.Relationship.Other
        };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? ToDateOnly(DateTime? value)
    {
        if (value is not { } dt || dt == default)
        {
            return null;
        }

        return DateOnly.FromDateTime(dt);
    }

    private static DateOnly? ToDateOnly(DateTime value) =>
        value == default ? null : DateOnly.FromDateTime(value);

    private static DateTime? ToDateTime(DateOnly? value) =>
        value is { } d ? d.ToDateTime(TimeOnly.MinValue) : null;
}
