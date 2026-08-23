using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

internal static class GatewayClaimFixtures
{
    public static GatewayClaimSubmissionRequest Professional(
        string payerId = "60054",
        string claimId = "CLM-P-1001",
        int version = 1,
        string frequency = "1") =>
        new()
        {
            TenantId = "tenant-alpha",
            ClaimId = claimId,
            ClaimVersion = version,
            ClaimType = GatewayClaimType.Professional,
            FrequencyCode = frequency,
            PayerId = payerId,
            PayerName = "Synthetic Eligible Payer",
            PlaceOfServiceCode = "11",
            TotalCharge = 109.20m,
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            ServiceDateTo = new DateOnly(2026, 1, 15),
            CorrelationId = "corr-claim-1",
            BillingProvider = new GatewayClaimProvider
            {
                Npi = "1999999984",
                OrganizationName = "Therapy Associates",
                EmployerId = "123456789",
                Address1 = "123 Some St",
                City = "A City",
                State = "NY",
                PostalCode = "123450000",
                Phone = "5553334444"
            },
            RenderingProvider = new GatewayClaimProvider
            {
                Npi = "1999999984",
                OrganizationName = "Therapy Associates"
            },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "U7777788888",
                FirstName = "John",
                LastName = "Anon",
                DateOfBirth = new DateOnly(2000, 1, 1)
            },
            Diagnoses = { new GatewayClaimDiagnosis { Code = "F1111", Qualifier = "ABK", PointerNumber = 1 } },
            ServiceLines =
            {
                new GatewayClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "90837",
                    Modifiers = { "95" },
                    DiagnosisPointers = { 1 },
                    Units = 1,
                    ChargeAmount = 109.20m,
                    ServiceDateFrom = new DateOnly(2026, 1, 15)
                }
            }
        };

    public static GatewayClaimSubmissionRequest Institutional() =>
        new()
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-I-2001",
            ClaimType = GatewayClaimType.Institutional,
            PayerId = "60054",
            PlaceOfServiceCode = "21",
            TypeOfBill = "111",
            TotalCharge = 500.00m,
            ServiceDateFrom = new DateOnly(2026, 2, 1),
            ServiceDateTo = new DateOnly(2026, 2, 3),
            AdmissionDate = new DateOnly(2026, 2, 1),
            BillingProvider = new GatewayClaimProvider
            {
                Npi = "1999999984",
                OrganizationName = "Demo Hospital",
                EmployerId = "123456789",
                Address1 = "1 Hospital Rd",
                City = "A City",
                State = "NY",
                PostalCode = "123450000"
            },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "MBR-INST",
                FirstName = "Jane",
                LastName = "Doe",
                DateOfBirth = new DateOnly(1980, 5, 5)
            },
            Diagnoses = { new GatewayClaimDiagnosis { Code = "R45851", Qualifier = "ABK" } },
            ServiceLines =
            {
                new GatewayClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "H0001",
                    RevenueCode = "0124",
                    Units = 1,
                    ChargeAmount = 500.00m,
                    ServiceDateFrom = new DateOnly(2026, 2, 1)
                }
            }
        };

    public static GatewayClaimSubmissionRequest Dental() =>
        new()
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-D-3001",
            ClaimType = GatewayClaimType.Dental,
            PayerId = "60054",
            PlaceOfServiceCode = "11",
            TotalCharge = 150.00m,
            ServiceDateFrom = new DateOnly(2026, 3, 1),
            BillingProvider = new GatewayClaimProvider
            {
                Npi = "1999999984",
                OrganizationName = "Demo Dental",
                EmployerId = "123456789",
                Address1 = "9 Smile Ave",
                City = "A City",
                State = "NY",
                PostalCode = "123450000"
            },
            Subscriber = new GatewayEligibilityPerson
            {
                MemberId = "MBR-DENT",
                FirstName = "Sam",
                LastName = "Smile",
                DateOfBirth = new DateOnly(1990, 7, 7)
            },
            ServiceLines =
            {
                new GatewayClaimLine
                {
                    LineNumber = 1,
                    ProcedureCode = "D0120",
                    Units = 1,
                    ChargeAmount = 150.00m,
                    ToothNumber = "14",
                    ToothSurface = "O",
                    ServiceDateFrom = new DateOnly(2026, 3, 1)
                }
            }
        };
}
