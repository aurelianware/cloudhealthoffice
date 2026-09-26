using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.ProviderEligibilityApi.Tests;

/// <summary>Synthetic fixtures only. Distinctive values let tests prove none leak into logs or responses.</summary>
internal static class EligibilityTestData
{
    public const string MemberId = "MBRZX9913377";
    public const string SubscriberFirst = "Quillon";
    public const string SubscriberLast = "Vantasserie";
    public const string SubscriberDob = "1961-04-17";
    public const string DependentFirst = "Orlaith";
    public const string DependentLast = "Vantasserie";
    public const string DependentDob = "2011-09-03";

    public static object SelfRequest() => new
    {
        payerId = "87726",
        provider = new { npi = "1999999984", organizationName = "3rd Set Smiles" },
        subscriber = new
        {
            memberId = MemberId,
            firstName = SubscriberFirst,
            lastName = SubscriberLast,
            dateOfBirth = SubscriberDob
        },
        serviceTypeCode = "35",
        correlationId = "booking-42"
    };

    public static object DependentRequest() => new
    {
        payerId = "87726",
        provider = new { npi = "1999999984" },
        subscriber = new
        {
            memberId = MemberId,
            firstName = SubscriberFirst,
            lastName = SubscriberLast,
            dateOfBirth = SubscriberDob
        },
        patient = new
        {
            firstName = DependentFirst,
            lastName = DependentLast,
            dateOfBirth = DependentDob,
            relationshipToSubscriber = "child"
        }
    };

    public static GatewayResponse<GatewayEligibilityResponse> ActiveCoverage() =>
        GatewayResponse<GatewayEligibilityResponse>.Success(
            new GatewayEligibilityResponse
            {
                IsEligible = true,
                CoverageStatus = GatewayCoverageStatus.Active,
                PlanName = "Dental PPO",
                PlanId = "PLAN-1",
                GroupNumber = "GRP-7",
                CoverageStart = new DateOnly(2026, 1, 1),
                Subscriber = new GatewayEligibilityPerson
                {
                    MemberId = MemberId,
                    FirstName = SubscriberFirst,
                    LastName = SubscriberLast,
                    DateOfBirth = DateOnly.Parse(SubscriberDob)
                },
                Benefits =
                {
                    new GatewayEligibilityBenefit
                    {
                        ServiceTypeCode = "35",
                        ServiceTypeName = "Dental Care",
                        CoverageLevel = "Individual",
                        TimePeriod = "Remaining",
                        Amount = 1250m
                    }
                }
            },
            Metadata(GatewayTransactionStatus.Completed, GatewayErrorCategory.None));

    public static GatewayResponse<GatewayEligibilityResponse> PayerRejection() =>
        GatewayResponse<GatewayEligibilityResponse>.Success(
            new GatewayEligibilityResponse
            {
                CoverageStatus = GatewayCoverageStatus.Unknown,
                RejectionReason = "Invalid/Missing Subscriber/Insured ID",
                Subscriber = new GatewayEligibilityPerson { MemberId = MemberId, LastName = SubscriberLast }
            },
            Metadata(GatewayTransactionStatus.Rejected, GatewayErrorCategory.PayerRejected));

    public static GatewayResponse<GatewayEligibilityResponse> Failure(GatewayErrorCategory category) =>
        GatewayResponse<GatewayEligibilityResponse>.Failure(
            $"Gateway failure ({category}).",
            Metadata(
                category == GatewayErrorCategory.Timeout ? GatewayTransactionStatus.TimedOut : GatewayTransactionStatus.Failed,
                category));

    private static GatewayTransactionMetadata Metadata(GatewayTransactionStatus status, GatewayErrorCategory category) =>
        new()
        {
            GatewayName = "Stedi",
            TransactionType = HealthcareTransactionType.Eligibility270271,
            Status = status,
            ErrorCategory = category,
            CorrelationId = "booking-42",
            TenantId = ProviderEligibilityApiFactory.PracticeTenant,
            Latency = TimeSpan.FromMilliseconds(420)
        };
}
