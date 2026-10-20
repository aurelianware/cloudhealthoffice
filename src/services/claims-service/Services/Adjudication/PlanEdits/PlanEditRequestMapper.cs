using System.Globalization;
using ClaimsService.Models;
using CloudHealthOffice.PlanEdits.Contracts;

namespace ClaimsService.Services.Adjudication.PlanEdits;

/// <summary>
/// The claim, as the plan's edit service sees it: the facts edits are written
/// against. Names, addresses, pay-to details and free-text notes are never
/// sent (minimum necessary).
/// </summary>
public static class PlanEditRequestMapper
{
    public static PlanEditRequest Map(ClaimAdjudicationContext context, string requestId)
    {
        var claim = context.Claim;
        return new PlanEditRequest
        {
            RequestId = requestId,
            TenantId = context.TenantId,
            Purpose = context.ExaminerApproval is null
                ? PlanEditPurpose.Adjudication
                : PlanEditPurpose.ApprovalReadjudication,
            Claim = new PlanEditClaim
            {
                ClaimId = claim.Id,
                ClaimVersionId = context.ClaimVersionId,
                VersionNumber = claim.VersionNumber,
                ClaimNumber = claim.ClaimNumber,
                ClaimType = claim.ClaimType.ToString(),
                FrequencyCode = claim.ClaimFrequencyCode,
                LineOfBusiness = claim.LineOfBusiness.ToString(),
                BenefitPlanId = claim.BenefitPlanId,
                MemberId = claim.MemberId,
                SubscriberId = claim.SubscriberId,
                Member = context.ResolvedMember is { } member
                    ? new PlanEditMember
                    {
                        BirthDate = Date(member.DateOfBirth),
                        Gender = string.IsNullOrWhiteSpace(member.Gender) ? null : member.Gender,
                    }
                    : null,
                BillingProviderNpi = claim.BillingProviderNPI,
                RenderingProviderNpi = claim.RenderingProviderNPI,
                FacilityNpi = claim.FacilityNPI,
                PlaceOfService = claim.PlaceOfServiceCode,
                ServiceDateFrom = Date(claim.ServiceDateFrom)!,
                ServiceDateTo = Date(claim.ServiceDateTo)!,
                TotalCharge = claim.TotalChargeAmount,
                PriorAuthorizationNumber = claim.PriorAuthorizationNumber,
                ReferralNumber = claim.ReferralNumber,
                Diagnoses = claim.DiagnosisCodes
                    .Select(d => new PlanEditDiagnosis { Code = d.Code, Qualifier = d.CodeQualifier, Pointer = d.PointerNumber })
                    .ToList(),
                Lines = claim.ClaimLines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new PlanEditLine
                    {
                        LineNumber = l.LineNumber,
                        ProcedureCode = l.ProcedureCode,
                        Modifiers = l.Modifiers.ToList(),
                        RevenueCode = l.RevenueCode,
                        Units = l.Units,
                        UnitType = l.UnitType,
                        Charge = l.ChargeAmount,
                        ServiceDateFrom = Date(l.ServiceDateFrom)!,
                        ServiceDateTo = Date(l.ServiceDateTo)!,
                        PlaceOfService = l.PlaceOfServiceCode,
                        DiagnosisPointers = l.DiagnosisPointers.ToList(),
                        NationalDrugCode = l.NationalDrugCode,
                        PriorAuthorizationNumber = l.PriorAuthorizationNumber,
                    })
                    .ToList(),
            },
        };
    }

    private static string? Date(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
