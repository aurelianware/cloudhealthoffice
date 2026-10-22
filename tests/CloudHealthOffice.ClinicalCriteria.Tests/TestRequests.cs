using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;

namespace CloudHealthOffice.ClinicalCriteria.Tests;

internal static class TestRequests
{
    public static CriteriaRequest Inpatient(
        string tenantId = "tenant-a",
        string stateCode = "TX",
        PaLineOfBusiness lob = PaLineOfBusiness.Medicaid,
        string? program = null,
        CriteriaReviewType reviewType = CriteriaReviewType.Prospective) => new()
    {
        TenantId              = tenantId,
        StateCode             = stateCode,
        Lob                   = lob,
        Program               = program,
        ReviewType            = reviewType,
        MemberId              = "M100",
        MemberDateOfBirth     = new DateOnly(1970, 4, 2),
        ServiceDate           = new DateOnly(2026, 10, 22),
        ProcedureCodes        = ["99222"],
        DiagnosisCodes        = ["J18.9"],
        PlaceOfServiceCode    = "21",
        RequestedUnits        = 3,
        LevelOfCare           = "inpatient-acute",
        RequestingProviderNpi = "1234567893",
        ServicingProviderNpi  = "1234567893",
        EstimatedCost         = 14_500m,
    };
}
