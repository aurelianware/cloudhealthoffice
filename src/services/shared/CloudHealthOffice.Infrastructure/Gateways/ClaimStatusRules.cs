using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Vendor-neutral 276 request assembly, validation, and 277 status
/// normalization. Mapping from Stedi JSON lives in the Stedi adapter.
/// </summary>
internal static class ClaimStatusRules
{
    public static void CaptureSource(ClaimTransmissionRecord record, GatewayClaimSubmissionRequest request)
    {
        record.PayerId ??= request.PayerId;
        record.ServiceDateFrom = request.ServiceDateFrom;
        record.ServiceDateTo = request.ServiceDateTo ?? request.ServiceDateFrom;
        record.ClaimAmount = request.TotalCharge;
        record.TypeOfBill = request.TypeOfBill;
        record.InquirySource = ClaimStatusInquirySource.FromSubmission(request);
        if (string.IsNullOrWhiteSpace(record.PatientControlNumber) && !string.IsNullOrWhiteSpace(request.ClaimId))
        {
            record.PatientControlNumber = request.ClaimId.Length <= 20 ? request.ClaimId : request.ClaimId[..20];
        }
    }

    public static void ApplyToRequest(ClaimStatusRequest request, ClaimTransmissionRecord transmission)
    {
        request.TenantId = transmission.TenantId;
        request.ClaimId = FirstNonBlank(request.ClaimId, transmission.ClaimId);
        request.TransmissionId = transmission.TransmissionId;
        request.PayerId = FirstNonBlank(request.PayerId, transmission.PayerId);
        request.ClaimType ??= transmission.ClaimType;
        request.PatientControlNumber = FirstNonBlank(
            request.PatientControlNumber, transmission.PatientControlNumber, transmission.ClaimId);
        request.PayerClaimControlNumber = FirstNonBlank(
            request.PayerClaimControlNumber, transmission.PayerClaimControlNumber);
        request.ServiceDateFrom ??= transmission.ServiceDateFrom ?? transmission.InquirySource?.ServiceDateFrom;
        request.ServiceDateTo ??= transmission.ServiceDateTo ?? transmission.InquirySource?.ServiceDateTo
                                  ?? request.ServiceDateFrom;
        request.ClaimAmount ??= transmission.ClaimAmount ?? transmission.InquirySource?.ClaimAmount;
        request.TypeOfBill = FirstNonBlank(request.TypeOfBill, transmission.TypeOfBill, transmission.InquirySource?.TypeOfBill);
        request.GroupNumber = FirstNonBlank(request.GroupNumber, transmission.InquirySource?.GroupNumber);
        request.Provider ??= ClaimStatusInquirySource.CloneProvider(transmission.InquirySource?.BillingProvider);
        request.Subscriber ??= ClaimStatusInquirySource.ClonePerson(transmission.InquirySource?.Subscriber);
        request.Patient ??= ClaimStatusInquirySource.ClonePerson(transmission.InquirySource?.Patient);
        if (request.ServiceLines.Count == 0 && transmission.InquirySource is { ServiceLines.Count: > 0 })
        {
            request.ServiceLines = transmission.InquirySource.Clone().ServiceLines;
        }
    }

    public static (GatewayErrorCategory Category, string Message)? Validate(
        ClaimStatusRequest request, ClaimTransmissionRecord? transmission)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return (GatewayErrorCategory.Validation, "TenantId is required.");
        }

        if (transmission is not null &&
            !string.Equals(transmission.TenantId, request.TenantId, StringComparison.Ordinal))
        {
            return (GatewayErrorCategory.ClaimMismatch, "Tenant does not match the claim transmission.");
        }

        if (transmission is not null &&
            !string.IsNullOrWhiteSpace(request.ClaimId) &&
            !string.Equals(transmission.ClaimId, request.ClaimId, StringComparison.Ordinal))
        {
            return (GatewayErrorCategory.ClaimMismatch, "ClaimId does not match the claim transmission.");
        }

        if (request.Provider is null || !request.Provider.HasNpi)
        {
            return (GatewayErrorCategory.Validation, "Billing provider NPI is required.");
        }

        if (request.Subscriber is null ||
            string.IsNullOrWhiteSpace(request.Subscriber.MemberId) ||
            string.IsNullOrWhiteSpace(request.Subscriber.LastName) ||
            string.IsNullOrWhiteSpace(request.Subscriber.FirstName))
        {
            return (GatewayErrorCategory.Validation, "Subscriber member id, first name, and last name are required.");
        }

        if (request.ServiceDateFrom is null && request.ServiceDateTo is null)
        {
            return (GatewayErrorCategory.Validation, "Service dates are required.");
        }

        if (request.ServiceLineNumber is int line)
        {
            if (line <= 0)
            {
                return (GatewayErrorCategory.Validation, "ServiceLineNumber must be a positive line number.");
            }

            var known = request.ServiceLines.Any(l => l.LineNumber == line) ||
                        (transmission?.ServiceLineNumbers.Contains(line) ?? false);
            if (!known)
            {
                return (GatewayErrorCategory.ServiceLineNotFound,
                    "Service line was not present on the original submitted claim.");
            }
        }

        return null;
    }

    public static GatewayClaimStatus Normalize(
        string? statusCategoryCode,
        string? statusCode,
        decimal? paidAmount,
        decimal? submittedAmount)
    {
        var category = (statusCategoryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (category.Length == 0)
        {
            return GatewayClaimStatus.Unknown;
        }

        if (category is "A4")
        {
            return GatewayClaimStatus.NoRecordFound;
        }

        if (category is "A3")
        {
            return GatewayClaimStatus.Rejected;
        }

        if (category is "A2")
        {
            return GatewayClaimStatus.Accepted;
        }

        if (category.StartsWith('A'))
        {
            return GatewayClaimStatus.Received;
        }

        if (category is "P2" or "P3" or "P4")
        {
            return GatewayClaimStatus.Pending;
        }

        if (category.StartsWith('P'))
        {
            return GatewayClaimStatus.InProcess;
        }

        if (category is "F2")
        {
            return GatewayClaimStatus.Denied;
        }

        if (category is "F1")
        {
            if (paidAmount is > 0 && submittedAmount is > 0 && paidAmount < submittedAmount)
            {
                return GatewayClaimStatus.PartiallyPaid;
            }

            if (paidAmount is > 0 || IsPaidStatusCode(statusCode))
            {
                return GatewayClaimStatus.Paid;
            }

            return GatewayClaimStatus.Finalized;
        }

        if (category.StartsWith('F'))
        {
            return GatewayClaimStatus.Finalized;
        }

        if (category.StartsWith('R') || category.StartsWith('D'))
        {
            return GatewayClaimStatus.AdditionalInformationRequested;
        }

        return GatewayClaimStatus.Unknown;
    }

    public static bool IsFollowUpCandidate(
        ClaimTransmissionRecord transmission,
        ClaimStatusInquiryRecord? latest)
    {
        if (transmission.Status is GatewayClaimTransmissionStatus.AcknowledgmentRejected
            or GatewayClaimTransmissionStatus.AcknowledgmentFailed)
        {
            return false;
        }

        if (transmission.Status is not (GatewayClaimTransmissionStatus.AcknowledgmentAccepted
            or GatewayClaimTransmissionStatus.AcknowledgmentPartial
            or GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway))
        {
            return false;
        }

        return latest?.NormalizedStatus is not (
            GatewayClaimStatus.Paid or
            GatewayClaimStatus.PartiallyPaid or
            GatewayClaimStatus.Denied or
            GatewayClaimStatus.Finalized or
            GatewayClaimStatus.Rejected);
    }

    public static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsPaidStatusCode(string? statusCode)
    {
        var code = (statusCode ?? string.Empty).Trim();
        return code is "65" or "102" or "2";
    }
}
