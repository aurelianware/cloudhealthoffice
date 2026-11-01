using EncounterSubmissionService.StateRoutes;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

internal static class StateRouteTestData
{
    public const string BillingNpi = "1234567893";
    public const string RenderingNpi = "1245319599";

    public static StateEncounterRouteOptions TexasOptions(bool withTenant = true)
    {
        var o = new StateEncounterRouteOptions
        {
            Enabled = true,
            ReceiverId = "TMHPTESTRCV",
            ReceiverName = "TMHP",
            DestinationPayerId = "TXMCD",
            DestinationPayerName = "TEXAS HHSC",
            UsageIndicator = "T"
        };
        if (withTenant)
            o.Tenants["tenant-1"] = new StateEncounterTenantOptions
            {
                SubmitterId = "TPID12345",
                SubmitterName = "SAMPLE HEALTH PLAN",
                ContactName = "EDI TEAM",
                ContactPhone = "512-555-0100",
                PlanName = "SAMPLE HEALTH PLAN",
                PlanPayerId = "MCO123",
                PlanCode = "4A"
            };
        return o;
    }

    public static StateEncounterRouteOptions CaliforniaOptions()
    {
        var o = TexasOptions();
        o.ReceiverId = "DHCSTESTRCV";
        o.ReceiverName = "DHCS";
        o.DestinationPayerId = "CAMCD";
        o.DestinationPayerName = "CA DHCS";
        return o;
    }

    public static StateEncounterRouteRegistry Registry(
        StateEncounterRouteOptions? tx = null, StateEncounterRouteOptions? ca = null)
    {
        var d = new Dictionary<string, StateEncounterRouteOptions>();
        if (tx is not null) d["TX"] = tx;
        if (ca is not null) d["CA"] = ca;
        return new StateEncounterRouteRegistry(d);
    }

    public static Encounter837Envelope Envelope(long icn = 42) => new()
    {
        SenderIdQualifier = "ZZ",
        SenderId = "TPID12345",
        ReceiverIdQualifier = "ZZ",
        ReceiverId = "TMHPTESTRCV",
        SubmitterName = "SAMPLE HEALTH PLAN",
        ContactName = "EDI TEAM",
        ContactPhone = "5125550100",
        ReceiverName = "TMHP",
        DestinationPayerName = "TEXAS HHSC",
        DestinationPayerId = "TXMCD",
        DestinationPayerResponsibility = "S",
        DestinationClaimFilingIndicator = "MC",
        PlanName = "SAMPLE HEALTH PLAN",
        PlanPayerId = "MCO123",
        PlanPayerResponsibility = "P",
        PlanClaimFilingIndicator = "HM",
        UsageIndicator = "T",
        InterchangeControlNumber = icn,
        CreatedAtUtc = new DateTime(2026, 11, 1, 14, 30, 0, DateTimeKind.Utc)
    };

    public static EncounterClaim Professional(string id = "sub-1") => new()
    {
        SubmissionId = id,
        ClaimId = "claim-" + id,
        PatientControlNumber = "CLM-" + id,
        Form = EncounterFormType.Professional,
        TotalCharge = 250m,
        PlaceOfService = "11",
        ServiceFrom = new DateOnly(2026, 10, 3),
        ServiceTo = new DateOnly(2026, 10, 3),
        PriorAuthorizationNumber = "PA-1",
        Diagnoses = new[] { new EncounterDiagnosis("J45.909"), new EncounterDiagnosis("R05.9") },
        Member = Member(),
        BillingProvider = Billing(),
        RenderingNpi = RenderingNpi,
        RenderingLastName = "SMITH",
        RenderingFirstName = "JANE",
        PlanPaidAmount = 120m,
        PlanAdjudicationDate = new DateOnly(2026, 10, 20),
        Lines = new[]
        {
            new EncounterLine
            {
                LineNumber = 1, ProcedureCode = "99213", Modifiers = new[] { "25" },
                Charge = 150m, Units = 1, DiagnosisPointers = new[] { 1, 2 },
                ServiceFrom = new DateOnly(2026, 10, 3), ServiceTo = new DateOnly(2026, 10, 3),
                PlaceOfService = "11",
                PaidAmount = 90m,
                Adjustments = new[] { new EncounterAdjustment("CO", "45", 60m) }
            },
            new EncounterLine
            {
                LineNumber = 2, ProcedureCode = "94640", Charge = 100m, Units = 1,
                DiagnosisPointers = new[] { 1 },
                ServiceFrom = new DateOnly(2026, 10, 3), ServiceTo = new DateOnly(2026, 10, 3),
                PaidAmount = 30m,
                Adjustments = new[] { new EncounterAdjustment("CO", "45", 65m), new EncounterAdjustment("PR", "3", 5m) }
            }
        }
    };

    public static EncounterClaim Institutional(string id = "sub-2") => new()
    {
        SubmissionId = id,
        ClaimId = "claim-" + id,
        PatientControlNumber = "CLM-" + id,
        Form = EncounterFormType.Institutional,
        TotalCharge = 5000m,
        FacilityTypeCode = "11",
        ServiceFrom = new DateOnly(2026, 9, 28),
        ServiceTo = new DateOnly(2026, 10, 1),
        AdmissionDate = new DateOnly(2026, 9, 28),
        AdmissionHour = "0830",
        AdmissionTypeCode = "1",
        AdmissionSourceCode = "7",
        PatientStatusCode = "01",
        Diagnoses = new[] { new EncounterDiagnosis("J18.9", "Y"), new EncounterDiagnosis("E11.9", "N") },
        Member = Member(),
        BillingProvider = Billing(),
        RenderingNpi = RenderingNpi,
        RenderingLastName = "JONES",
        RenderingFirstName = "AL",
        PlanPaidAmount = 3200m,
        PlanAdjudicationDate = new DateOnly(2026, 10, 15),
        Lines = new[]
        {
            new EncounterLine
            {
                LineNumber = 1, ProcedureCode = "", RevenueCode = "0120", Charge = 4000m, Units = 3,
                UnitType = "DA", ServiceFrom = new DateOnly(2026, 9, 28), ServiceTo = new DateOnly(2026, 10, 1),
                PaidAmount = 2800m, Adjustments = new[] { new EncounterAdjustment("CO", "45", 1200m) }
            },
            new EncounterLine
            {
                LineNumber = 2, ProcedureCode = "71046", RevenueCode = "0324", Charge = 1000m, Units = 1,
                ServiceFrom = new DateOnly(2026, 9, 29), ServiceTo = new DateOnly(2026, 9, 29),
                PaidAmount = 400m, Adjustments = new[] { new EncounterAdjustment("CO", "45", 600m) }
            }
        }
    };

    public static EncounterMember Member() => new()
    {
        MemberId = "512345678",
        GroupNumber = "STAR",
        LastName = "DOE",
        FirstName = "ALEX",
        DateOfBirth = new DateOnly(2015, 4, 2),
        Gender = "F",
        AddressLine = "100 MAIN ST",
        City = "AUSTIN",
        State = "TX",
        PostalCode = "78701"
    };

    public static EncounterProvider Billing() => new()
    {
        Npi = BillingNpi,
        EntityType = "2",
        LastOrOrganizationName = "AUSTIN PEDIATRICS PA",
        TaxId = "74-1234567",
        TaxonomyCode = "208000000X",
        AddressLine = "200 CONGRESS AVE",
        City = "AUSTIN",
        State = "TX",
        PostalCode = "78701-4000"
    };
}
