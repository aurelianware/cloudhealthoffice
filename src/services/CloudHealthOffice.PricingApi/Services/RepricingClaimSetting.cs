using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.ReferenceData.Domain;

namespace CloudHealthOffice.PricingApi.Services;

/// <summary>
/// How a repricing request's claim type and type of bill decide the claim's setting,
/// the same way claims adjudication decides it (ADR 016): an institutional (837I)
/// claim, or a claim with a valid NUBC type of bill, always takes the facility rate
/// (engine <c>PricingRequest.IsInstitutional</c> / <c>BillType</c>); a professional or
/// dental claim is priced by place of service.
/// </summary>
public static class RepricingClaimSetting
{
    /// <summary>
    /// The claim type to price: the request's, or — when it sent none — institutional
    /// for a valid type of bill and professional otherwise (the behaviour before the
    /// field was optional, for every request that sent no type of bill).
    /// </summary>
    public static ClaimType ResolveClaimType(RepricingRequest request)
        => request.ClaimType
           ?? (NubcTypeOfBill.IsValid(request.BillType) ? ClaimType.Institutional : ClaimType.Professional);

    /// <summary>True for the 837I claim types: institutional, outpatient and inpatient.</summary>
    public static bool IsInstitutional(ClaimType claimType)
        => claimType is ClaimType.Institutional or ClaimType.Outpatient or ClaimType.Inpatient;

    /// <summary>The request's type of bill, normalized to three digits; null when absent or malformed.</summary>
    public static string? NormalizedBillType(RepricingRequest request)
        => NubcTypeOfBill.Normalize(request.BillType);

    /// <summary>
    /// The 400 error for a request whose claim type and type of bill cannot be priced
    /// as sent; null when the request is valid. A malformed type of bill is rejected
    /// rather than silently priced at the professional rate, and a type of bill on a
    /// professional or dental claim is rejected as contradictory.
    /// </summary>
    public static ApiError? Validate(RepricingRequest request)
    {
        if (request.ClaimType is { } type && !Enum.IsDefined(type))
        {
            return new ApiError
            {
                Code = "INVALID_CLAIM_TYPE",
                Message = "claimType must be one of: professional, institutional, outpatient, inpatient, dental.",
            };
        }

        if (string.IsNullOrWhiteSpace(request.BillType))
            return null;

        if (!NubcTypeOfBill.IsValid(request.BillType))
        {
            return new ApiError
            {
                Code = "INVALID_BILL_TYPE",
                Message = "billType must be a NUBC type of bill: three digits (e.g. \"131\") or four with a leading zero (\"0131\").",
            };
        }

        if (request.ClaimType is ClaimType.Professional or ClaimType.Dental)
        {
            return new ApiError
            {
                Code = "INVALID_BILL_TYPE",
                Message = $"billType applies to institutional claims only; claimType is {request.ClaimType}.",
            };
        }

        return null;
    }
}
