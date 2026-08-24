using System.Globalization;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Maps CHO canonical 276/277 models to Stedi's documented Real-Time Claim
/// Status JSON (API version 2024-04-01, path claimstatus/v2) and back.
/// Stedi DTOs stay internal.
/// </summary>
internal static class StediClaimStatusMapper
{
    public static StediClaimStatusRequestDto ToStediRequest(
        ClaimStatusRequest request, string tradingPartnerServiceId)
    {
        var dto = new StediClaimStatusRequestDto
        {
            TradingPartnerServiceId = tradingPartnerServiceId,
            Providers = new List<StediClaimStatusProviderDto>
            {
                ToProvider(request.Provider)
            },
            Subscriber = ToSubscriber(request.Subscriber, request.GroupNumber),
            Encounter = ToEncounter(request)
        };

        if (IsDependent(request.Patient, request.Subscriber))
        {
            dto.Dependent = ToDependent(request.Patient!);
        }

        if (request.ServiceLineNumber is int lineNumber)
        {
            var line = request.ServiceLines.FirstOrDefault(l => l.LineNumber == lineNumber);
            if (line is null)
            {
                throw new InvalidOperationException(
                    "Service-line inquiry is missing original line details and cannot be sent as claim-level status.");
            }

            dto.ServiceLinesInformation = new List<StediClaimStatusServiceLineDto>
            {
                ToServiceLine(line, request.ClaimType)
            };
        }

        return dto;
    }

    public static ClaimStatusResponse ToCanonical(
        StediClaimStatusResponseDto dto, ClaimStatusRequest request)
    {
        var claims = dto.Claims ?? new List<StediClaimStatusClaimDto>();
        var matched = SelectClaim(claims, request);
        var detail = matched?.ClaimStatus;
        var errors = dto.Errors ?? new List<StediClaimStatusErrorDto>();

        var category = detail?.StatusCategoryCode ?? FirstErrorCode(errors);
        var code = detail?.StatusCode;
        var paid = ParseAmount(detail?.AmountPaid);
        var submitted = ParseAmount(detail?.SubmittedAmount) ?? request.ClaimAmount;
        var status = claims.Count == 0 && errors.Count == 0
            ? GatewayClaimStatus.NoRecordFound
            : InferBusinessStatus(category, code, paid, submitted, errors);

        var response = new ClaimStatusResponse
        {
            ClaimId = request.ClaimId,
            TransmissionId = request.TransmissionId,
            Status = status,
            StatusCategoryCode = category,
            StatusCode = code,
            StatusDescription = FirstNonBlank(
                detail?.StatusCodeValue, detail?.StatusCategoryCodeValue, detail?.Message,
                errors.FirstOrDefault()?.Description),
            PayerClaimControlNumber = FirstNonBlank(
                detail?.TradingPartnerClaimNumber, request.PayerClaimControlNumber),
            PatientControlNumber = FirstNonBlank(
                detail?.PatientAccountNumber, request.PatientControlNumber),
            EffectiveDate = ParseDate(detail?.EffectiveDate ?? detail?.StatusInformationEffectiveDate),
            StatusDate = ParseDate(detail?.StatusInformationEffectiveDate ?? detail?.EffectiveDate),
            ClaimAmount = submitted,
            PaidAmount = paid,
            ExternalTransactionId = dto.Meta?.TransactionId ?? dto.Meta?.TraceId ?? dto.ControlNumber,
            MatchCount = claims.Count,
            ServiceLineStatuses = MapLines(matched),
            Messages = errors
                .Select(e => new ClaimStatusMessage { Code = e.Code, Description = e.Description })
                .ToList()
        };

        if (!string.IsNullOrWhiteSpace(detail?.Message) &&
            response.Messages.All(m => m.Description != detail.Message))
        {
            response.Messages.Add(new ClaimStatusMessage { Description = detail.Message });
        }

        return response;
    }

    public static GatewayErrorCategory BusinessCategory(ClaimStatusResponse response)
    {
        if (response.Status == GatewayClaimStatus.NoRecordFound)
        {
            return GatewayErrorCategory.None;
        }

        if (IsUnableToRespond(response))
        {
            return GatewayErrorCategory.ClaimStatusUnavailable;
        }

        var code = (response.StatusCategoryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (LooksLikeInvalidIdentifier(response) || code.StartsWith('E'))
        {
            return GatewayErrorCategory.PayerRejected;
        }

        return GatewayErrorCategory.None;
    }

    private static StediClaimStatusProviderDto ToProvider(GatewayClaimProvider? provider) =>
        new()
        {
            Npi = provider?.Npi,
            OrganizationName = provider?.OrganizationName,
            FirstName = provider?.FirstName,
            LastName = provider?.LastName,
            TaxId = DigitsOnly(provider?.EmployerId, 9),
            ProviderType = "BillingProvider"
        };

    private static StediClaimStatusSubscriberDto ToSubscriber(
        GatewayEligibilityPerson? subscriber, string? groupNumber) =>
        new()
        {
            FirstName = subscriber?.FirstName,
            LastName = subscriber?.LastName,
            DateOfBirth = FormatDate(subscriber?.DateOfBirth),
            Gender = NormalizeGender(subscriber?.Gender),
            MemberId = subscriber?.MemberId,
            GroupNumber = groupNumber
        };

    private static StediClaimStatusDependentDto ToDependent(GatewayEligibilityPerson patient) =>
        new()
        {
            FirstName = patient.FirstName,
            LastName = patient.LastName,
            DateOfBirth = FormatDate(patient.DateOfBirth),
            Gender = NormalizeGender(patient.Gender)
        };

    private static StediClaimStatusEncounterDto ToEncounter(ClaimStatusRequest request)
    {
        var (from, to) = ExpandServiceDates(request.ServiceDateFrom, request.ServiceDateTo);
        var encounter = new StediClaimStatusEncounterDto
        {
            BeginningDateOfService = FormatDate(from),
            EndDateOfService = FormatDate(to),
            SubmittedAmount = FormatAmount(request.ClaimAmount),
            BillingType = request.TypeOfBill
        };

        // Prefer the payer-assigned control number. Fall back to the patient
        // control number only when the payer number is not available — Stedi
        // documents that extra identifiers can hurt matching.
        if (!string.IsNullOrWhiteSpace(request.PayerClaimControlNumber))
        {
            encounter.TradingPartnerClaimNumber = request.PayerClaimControlNumber;
        }
        else if (!string.IsNullOrWhiteSpace(request.PatientControlNumber))
        {
            encounter.PatientAccountNumber = request.PatientControlNumber;
        }

        return encounter;
    }

    private static StediClaimStatusServiceLineDto ToServiceLine(
        ClaimStatusLineSource line, GatewayClaimType? claimType) =>
        new()
        {
            LineItemChargeAmount = FormatAmount(line.ChargeAmount) ?? "0.00",
            LineItemControlNumber = line.LineItemControlNumber ??
                                    (line.LineNumber > 0 ? line.LineNumber.ToString(CultureInfo.InvariantCulture) : null),
            ProcedureCode = line.ProcedureCode,
            ProcedureModifiers = line.Modifiers.Count == 0 ? null : line.Modifiers,
            ProductOrServiceIDQualifier = claimType == GatewayClaimType.Dental ? "AD" : "HC",
            RevenueCode = line.RevenueCode,
            ServiceLineDate = FormatDate(line.ServiceDateFrom),
            ServiceLineEndDate = FormatDate(line.ServiceDateTo ?? line.ServiceDateFrom),
            UnitsOfServiceCount = line.Units.ToString("0.##", CultureInfo.InvariantCulture)
        };

    private static StediClaimStatusClaimDto? SelectClaim(
        List<StediClaimStatusClaimDto> claims, ClaimStatusRequest request)
    {
        if (claims.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.PayerClaimControlNumber))
        {
            var byPayer = claims.FirstOrDefault(c =>
                string.Equals(
                    c.ClaimStatus?.TradingPartnerClaimNumber,
                    request.PayerClaimControlNumber,
                    StringComparison.OrdinalIgnoreCase));
            if (byPayer is not null)
            {
                return byPayer;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.PatientControlNumber))
        {
            var byPatient = claims.FirstOrDefault(c =>
                string.Equals(
                    c.ClaimStatus?.PatientAccountNumber,
                    request.PatientControlNumber,
                    StringComparison.OrdinalIgnoreCase));
            if (byPatient is not null)
            {
                return byPatient;
            }
        }

        return claims[0];
    }

    private static List<ClaimStatusLineResult> MapLines(StediClaimStatusClaimDto? claim)
    {
        if (claim?.ServiceDetails is null || claim.ServiceDetails.Count == 0)
        {
            return new List<ClaimStatusLineResult>();
        }

        return claim.ServiceDetails.Select(detail =>
        {
            var status = detail.Status?.FirstOrDefault();
            var paid = ParseAmount(detail.AmountPaid);
            var submitted = ParseAmount(detail.SubmittedAmount);
            return new ClaimStatusLineResult
            {
                LineItemControlNumber = detail.LineItemControlNumber,
                LineNumber = ParseLineNumber(detail.LineItemControlNumber),
                ProcedureCode = FirstNonBlank(detail.ProcedureCode, detail.ProcedureId),
                Status = ClaimStatusRules.Normalize(
                    status?.StatusCategoryCode, status?.StatusCode, paid, submitted),
                StatusCategoryCode = status?.StatusCategoryCode,
                StatusCode = status?.StatusCode,
                StatusDescription = FirstNonBlank(status?.StatusCodeValue, status?.StatusCategoryCodeValue),
                SubmittedAmount = submitted,
                PaidAmount = paid
            };
        }).ToList();
    }

    private static GatewayClaimStatus InferBusinessStatus(
        string? category,
        string? code,
        decimal? paid,
        decimal? submitted,
        List<StediClaimStatusErrorDto> errors)
    {
        var normalized = ClaimStatusRules.Normalize(category, code, paid, submitted);
        if (normalized != GatewayClaimStatus.Unknown)
        {
            return normalized;
        }

        if (errors.Count == 0)
        {
            return GatewayClaimStatus.Unknown;
        }

        var text = string.Join(' ', errors.Select(e => $"{e.Code} {e.Description}")).ToLowerInvariant();
        if (text.Contains("not found") || text.Contains("no match") || text.Contains("a4"))
        {
            return GatewayClaimStatus.NoRecordFound;
        }

        return GatewayClaimStatus.Unknown;
    }

    private static bool LooksLikeInvalidIdentifier(ClaimStatusResponse response)
    {
        var text = string.Join(' ', response.Messages.Select(m => $"{m.Code} {m.Description}"))
            .ToLowerInvariant();
        return text.Contains("invalid") &&
               (text.Contains("subscriber") || text.Contains("member") || text.Contains("claim"));
    }

    private static bool IsUnableToRespond(ClaimStatusResponse response)
    {
        var text = string.Join(' ', response.Messages.Select(m => $"{m.Code} {m.Description}"))
            .ToLowerInvariant();
        return text.Contains("unable to respond") || text.Contains("not available");
    }

    private static bool IsDependent(GatewayEligibilityPerson? patient, GatewayEligibilityPerson? subscriber)
    {
        if (patient is null || !patient.HasIdentity)
        {
            return false;
        }

        if (patient.IsSelf)
        {
            return false;
        }

        if (subscriber is null)
        {
            return true;
        }

        var sameName = string.Equals(patient.FirstName, subscriber.FirstName, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(patient.LastName, subscriber.LastName, StringComparison.OrdinalIgnoreCase);
        var sameDob = patient.DateOfBirth is not null && patient.DateOfBirth == subscriber.DateOfBirth;
        var sameMember = !string.IsNullOrWhiteSpace(patient.MemberId) &&
                         string.Equals(patient.MemberId, subscriber.MemberId, StringComparison.OrdinalIgnoreCase);
        return !(sameName && (sameDob || sameMember));
    }

    internal static (DateOnly From, DateOnly To) ExpandServiceDates(DateOnly? from, DateOnly? to)
    {
        var start = from ?? to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var end = to ?? from ?? start;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var expandedFrom = start.AddDays(-7);
        var expandedTo = end.AddDays(7);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (expandedTo > today)
        {
            expandedTo = today;
        }

        if (expandedTo < expandedFrom)
        {
            expandedTo = expandedFrom;
        }

        if (expandedTo.DayNumber - expandedFrom.DayNumber > 30)
        {
            expandedFrom = expandedTo.AddDays(-30);
        }

        return (expandedFrom, expandedTo);
    }

    private static string? NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender))
        {
            return null;
        }

        var value = gender.Trim().ToUpperInvariant();
        return value is "M" or "F" ? value : null;
    }

    private static string? FormatDate(DateOnly? date) =>
        date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static string? FormatAmount(decimal? amount) =>
        amount is null ? null : amount.Value.ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal? ParseAmount(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;

    private static int? ParseLineNumber(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;

    private static string? DigitsOnly(string? value, int length)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == length ? digits : null;
    }

    private static string? FirstErrorCode(List<StediClaimStatusErrorDto> errors) =>
        errors.Select(e => e.Code).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    private static string? FirstNonBlank(params string?[] values) =>
        ClaimStatusRules.FirstNonBlank(values);
}
