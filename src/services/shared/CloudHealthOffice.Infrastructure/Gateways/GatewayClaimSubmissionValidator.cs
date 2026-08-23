using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Deterministic pre-transmission checks shared by Mock and Stedi. A claim
/// that fails here is never sent to an external network.
/// </summary>
internal static class GatewayClaimSubmissionValidator
{
    public static string? Validate(GatewayClaimSubmissionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            return "TenantId is required.";
        }

        if (string.IsNullOrWhiteSpace(request.ClaimId))
        {
            return "ClaimId is required.";
        }

        if (request.ClaimVersion < 1)
        {
            return "ClaimVersion must be at least 1.";
        }

        if (string.IsNullOrWhiteSpace(request.PayerId))
        {
            return "PayerId is required.";
        }

        if (request.Subscriber is null || !request.Subscriber.HasIdentity)
        {
            return "Subscriber identity is required.";
        }

        if (request.BillingProvider is null || !request.BillingProvider.HasNpi)
        {
            return "Billing provider NPI is required.";
        }

        if (request.ServiceLines is null || request.ServiceLines.Count == 0)
        {
            return "At least one service line is required.";
        }

        if (request.ServiceLines.Any(l => string.IsNullOrWhiteSpace(l.ProcedureCode)))
        {
            return "Every service line requires a procedure code.";
        }

        var lineTotal = request.ServiceLines.Sum(l => l.ChargeAmount);
        if (lineTotal != request.TotalCharge)
        {
            return "Sum of service-line charges must equal TotalCharge.";
        }

        if (request.TotalCharge < 0)
        {
            return "TotalCharge must not be negative.";
        }

        if (string.IsNullOrWhiteSpace(request.PlaceOfServiceCode))
        {
            return "PlaceOfServiceCode is required.";
        }

        if (request.ClaimType == GatewayClaimType.Institutional)
        {
            if (string.IsNullOrWhiteSpace(request.TypeOfBill))
            {
                return "TypeOfBill is required for institutional claims.";
            }

            if (request.ServiceLines.All(l => string.IsNullOrWhiteSpace(l.RevenueCode)))
            {
                return "Institutional claims require a revenue code on at least one service line.";
            }
        }

        return null;
    }
}
