using System.Globalization;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Translates between Cloud Health Office canonical eligibility models and the
/// Stedi transport DTOs. This is the single boundary that references both sides;
/// no Stedi DTO travels past it.
///
/// The mapper only projects what the payer returned — it never invents coverage,
/// benefit, or accumulator values. Interpreting these values (applying benefits,
/// computing accumulators, adjudicating) remains Cloud Health Office's job.
/// </summary>
internal static class StediEligibilityMapper
{
    /// <summary>
    /// Build the Stedi request from the canonical request. The Stedi payer id
    /// has already been resolved from the canonical payer id by the caller.
    /// </summary>
    public static StediEligibilityRequestDto ToStediRequest(
        GatewayEligibilityRequest request, string stediPayerId)
    {
        var dto = new StediEligibilityRequestDto
        {
            TradingPartnerServiceId = stediPayerId,
            Provider = new StediProviderDto
            {
                Npi = NullIfBlank(request.ProviderNpi)
            },
            Subscriber = new StediSubscriberDto
            {
                MemberId = NullIfBlank(request.SubscriberId),
                FirstName = NullIfBlank(request.SubscriberFirstName),
                LastName = NullIfBlank(request.SubscriberLastName),
                DateOfBirth = FormatStediDate(request.SubscriberDateOfBirth),
                GroupNumber = NullIfBlank(request.GroupNumber)
            },
            ExternalPatientId = NullIfBlank(request.CorrelationId)
        };

        var encounter = new StediEncounterDto();
        if (!string.IsNullOrWhiteSpace(request.ServiceTypeCode))
        {
            encounter.ServiceTypeCodes = new List<string> { request.ServiceTypeCode };
        }

        if (request.ServiceDateTo is { } to && to != request.ServiceDate)
        {
            encounter.BeginningDateOfService = FormatStediDate(request.ServiceDate);
            encounter.EndDateOfService = FormatStediDate(to);
        }
        else
        {
            encounter.DateOfService = FormatStediDate(request.ServiceDate);
        }

        dto.Encounter = encounter;
        return dto;
    }

    /// <summary>Normalize a Stedi response into the canonical response.</summary>
    public static GatewayEligibilityResponse ToCanonicalResponse(StediEligibilityResponseDto stedi)
    {
        var response = new GatewayEligibilityResponse();

        // Payer business rejections (AAA) — coverage is not determinable.
        if (stedi.Errors is { Count: > 0 })
        {
            var reasons = stedi.Errors
                .Select(e => e.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList();
            response.RejectionReason = reasons.Count > 0
                ? string.Join("; ", reasons)
                : "Payer rejected the eligibility inquiry.";
            response.CoverageStatus = GatewayCoverageStatus.Unknown;
            response.IsEligible = false;
            return response;
        }

        var benefits = stedi.BenefitsInformation ?? new List<StediBenefitInformationDto>();

        var (status, statusCode) = DetermineCoverage(stedi.PlanStatus, benefits);
        response.CoverageStatus = status;
        response.StatusCode = statusCode;
        response.IsEligible = status == GatewayCoverageStatus.Active;
        if (status == GatewayCoverageStatus.Inactive)
        {
            response.RejectionReason = "Payer reports coverage is inactive for the service date.";
        }

        // Plan identity.
        response.GroupNumber = stedi.PlanInformation?.GroupNumber;
        response.PlanId = stedi.PlanInformation?.PlanNumber;
        response.PlanName = stedi.PlanInformation?.GroupDescription
            ?? stedi.PlanStatus?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PlanDetails))?.PlanDetails;

        // Coverage dates: prefer eligibility dates, fall back to plan dates.
        var planDates = stedi.PlanDateInformation;
        response.CoverageStart = ParseStediDate(planDates?.EligibilityBegin)
            ?? ParseStediDate(planDates?.PlanBegin);
        response.CoverageEnd = ParseStediDate(planDates?.EligibilityEnd)
            ?? ParseStediDate(planDates?.PlanEnd);

        response.Benefits = benefits.Select(ToBenefit).ToList();
        return response;
    }

    private static GatewayEligibilityBenefit ToBenefit(StediBenefitInformationDto b)
    {
        var amount = ParseDecimal(b.BenefitAmount);
        var percent = NormalizePercent(ParseDecimal(b.BenefitPercent));

        var benefit = new GatewayEligibilityBenefit
        {
            BenefitCode = NullIfBlank(b.Code),
            ServiceTypeCode = b.ServiceTypeCodes?.FirstOrDefault() ?? string.Empty,
            ServiceTypeName = b.Name ?? string.Empty,
            CoverageLevel = NullIfBlank(b.CoverageLevelCode),
            InNetwork = MapNetwork(b.InPlanNetworkIndicatorCode),
            TimePeriod = NullIfBlank(b.TimeQualifier) ?? NullIfBlank(b.TimeQualifierCode),
            Amount = amount,
            Percent = percent,
            Quantity = ParseDecimal(b.BenefitQuantity),
            AuthorizationRequired = MapAuthIndicator(b.AuthOrCertIndicator),
            Messages = b.AdditionalInformation?
                .Select(a => a.Description)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d!)
                .ToList() ?? new List<string>()
        };

        // Convenience projections for the two most common cost-share kinds.
        if (string.Equals(b.Code, "B", StringComparison.OrdinalIgnoreCase))
        {
            benefit.CopayAmount = amount;
        }
        if (string.Equals(b.Code, "A", StringComparison.OrdinalIgnoreCase))
        {
            benefit.CoinsurancePercent = percent;
        }

        return benefit;
    }

    private static (GatewayCoverageStatus Status, string StatusCode) DetermineCoverage(
        List<StediPlanStatusDto>? planStatus,
        List<StediBenefitInformationDto> benefits)
    {
        // Active-coverage EB01 code is "1"; inactive is "6". Prefer explicit
        // plan status, then fall back to the benefit lines.
        bool activeStatus = planStatus?.Any(p =>
            p.StatusCode == "1" ||
            (p.Status?.Contains("active", StringComparison.OrdinalIgnoreCase) == true &&
             p.Status?.Contains("inactive", StringComparison.OrdinalIgnoreCase) != true)) == true;

        bool inactiveStatus = planStatus?.Any(p =>
            p.StatusCode is "6" or "7" or "8" ||
            p.Status?.Contains("inactive", StringComparison.OrdinalIgnoreCase) == true) == true;

        bool activeBenefit = benefits.Any(b => b.Code == "1");
        bool inactiveBenefit = benefits.Any(b => b.Code is "6" or "I") && !activeBenefit;

        if (activeStatus || activeBenefit)
        {
            return (GatewayCoverageStatus.Active, "1");
        }

        if (inactiveStatus || inactiveBenefit)
        {
            return (GatewayCoverageStatus.Inactive, "6");
        }

        var firstCode = planStatus?.FirstOrDefault()?.StatusCode ?? string.Empty;
        return (GatewayCoverageStatus.Unknown, firstCode);
    }

    private static bool MapNetwork(string? indicator) =>
        // Y = in-network, N = out-of-network, W = not-applicable. Default to
        // in-network only when the payer says so or omits the field.
        !string.Equals(indicator, "N", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(indicator, "W", StringComparison.OrdinalIgnoreCase);

    private static bool? MapAuthIndicator(string? indicator) => indicator?.ToUpperInvariant() switch
    {
        "Y" => true,
        "N" => false,
        _ => null
    };

    private static decimal? NormalizePercent(decimal? value)
    {
        if (value is null)
        {
            return null;
        }
        // X12 EB08 is a fraction (0.20 = 20%). If a payer sends a whole number
        // (20), normalize it to a fraction.
        return value > 1m ? value.Value / 100m : value;
    }

    internal static DateOnly? ParseStediDate(string? yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd))
        {
            return null;
        }
        return DateOnly.TryParseExact(
            yyyymmdd.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    internal static string? FormatStediDate(DateOnly? date) =>
        date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
