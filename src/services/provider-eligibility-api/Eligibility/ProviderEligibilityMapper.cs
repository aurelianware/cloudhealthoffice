using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using ProviderEligibilityApi.Contracts;

namespace ProviderEligibilityApi.Eligibility;

internal static class ProviderEligibilityMapper
{
    public const string DefaultServiceTypeCode = "30";

    public static GatewayEligibilityRequest ToGatewayRequest(
        ProviderEligibilityCheckRequest request, string tenantId, DateOnly today)
    {
        var subscriber = request.Subscriber!;
        var gateway = new GatewayEligibilityRequest
        {
            TenantId = tenantId,
            PayerId = request.PayerId!.Trim(),
            ProviderNpi = request.Provider!.Npi!.Trim(),
            ProviderOrganizationName = Trim(request.Provider.OrganizationName),
            GroupNumber = Trim(request.GroupNumber),
            ServiceTypeCode = string.IsNullOrWhiteSpace(request.ServiceTypeCode)
                ? DefaultServiceTypeCode
                : request.ServiceTypeCode.Trim().ToUpperInvariant(),
            ServiceDate = request.ServiceDate ?? today,
            CorrelationId = request.CorrelationId ?? Guid.NewGuid().ToString("N"),
            SubscriberId = subscriber.MemberId!.Trim(),
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = subscriber.MemberId.Trim(),
                FirstName = Trim(subscriber.FirstName),
                LastName = Trim(subscriber.LastName),
                DateOfBirth = subscriber.DateOfBirth,
                RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
            }
        };

        if (request.Patient is { } patient)
        {
            gateway.Patient = new GatewayEligibilityPerson
            {
                MemberId = Trim(patient.MemberId),
                FirstName = Trim(patient.FirstName),
                LastName = Trim(patient.LastName),
                DateOfBirth = patient.DateOfBirth,
                RelationshipToSubscriber = string.IsNullOrWhiteSpace(patient.RelationshipToSubscriber)
                    ? GatewayEligibilityPerson.Relationship.Other
                    : patient.RelationshipToSubscriber.Trim().ToLowerInvariant()
            };
        }

        return gateway;
    }

    public static ProviderEligibilityCheckResponse ToResponse(
        GatewayResponse<GatewayEligibilityResponse> gateway, DateTimeOffset checkedAtUtc)
    {
        var metadata = gateway.Metadata;
        var result = gateway.Result;
        var response = new ProviderEligibilityCheckResponse
        {
            Outcome = metadata.Status switch
            {
                GatewayTransactionStatus.Completed when gateway.IsSuccess => "Completed",
                GatewayTransactionStatus.Rejected => "Rejected",
                _ => "Failed"
            },
            ErrorCategory = metadata.ErrorCategory.ToString(),
            CorrelationId = metadata.CorrelationId,
            CheckedAtUtc = checkedAtUtc
        };

        if (result is not null)
        {
            response.Eligible = result.IsEligible;
            response.CoverageStatus = result.CoverageStatus.ToString();
            response.PlanName = result.PlanName;
            response.PlanId = result.PlanId;
            response.GroupNumber = result.GroupNumber;
            response.CoverageStart = result.CoverageStart;
            response.CoverageEnd = result.CoverageEnd;
            response.Benefits = result.Benefits;
            response.Message = result.RejectionReason;
        }

        if (!gateway.IsSuccess)
        {
            response.Message = gateway.ErrorMessage ?? "The eligibility check could not be completed.";
        }

        return response;
    }

    /// <summary>
    /// Completed checks and payer business rejections are answers (200). Failures
    /// separate caller-fixable problems (422) from upstream or configuration
    /// problems the caller cannot fix (502/503).
    /// </summary>
    public static int ToStatusCode(GatewayResponse<GatewayEligibilityResponse> gateway)
    {
        if (gateway.IsSuccess || gateway.Metadata.Status == GatewayTransactionStatus.Rejected)
        {
            return StatusCodes.Status200OK;
        }

        return gateway.Metadata.ErrorCategory switch
        {
            GatewayErrorCategory.Validation or
            GatewayErrorCategory.PayerNotFound or
            GatewayErrorCategory.AmbiguousPayer or
            GatewayErrorCategory.InvalidPayer or
            GatewayErrorCategory.ExternalIdentifierMissing or
            GatewayErrorCategory.EnrollmentRequired or
            GatewayErrorCategory.NotSupported or
            GatewayErrorCategory.PayerRejected => StatusCodes.Status422UnprocessableEntity,

            GatewayErrorCategory.RateLimited or
            GatewayErrorCategory.ServiceUnavailable or
            GatewayErrorCategory.Connectivity or
            GatewayErrorCategory.Timeout or
            GatewayErrorCategory.ReferenceDataUnavailable => StatusCodes.Status503ServiceUnavailable,

            GatewayErrorCategory.Authentication or
            GatewayErrorCategory.Authorization or
            GatewayErrorCategory.Configuration or
            GatewayErrorCategory.MalformedResponse => StatusCodes.Status502BadGateway,

            _ => StatusCodes.Status500InternalServerError
        };
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
