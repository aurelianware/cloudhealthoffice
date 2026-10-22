namespace DemoPersonaSeeder;

/// <summary>
/// The demo cast. Every person, provider and company here is invented. Dates are
/// relative to <c>asOf</c> so the stories stay current whenever the seeder runs.
/// IDs are fixed, so the demo script can name them and a re-run finds them again.
/// </summary>
public static class PersonaCatalog
{
    public const string CommercialPlan = "commercial-ppo";
    public const string MedicaidPlan = "medicaid-hmo";
    public const string MedicarePlan = "medicare-advantage";

    public const string DesertSunGroup = "DSB-1001";
    public const string CopperStateGroup = "CSL-2001";
    public const string MedicaidGroup = "DEMO-MCD";
    public const string MedicareGroup = "DEMO-MA";

    public static DemoDataset Build(DateTime asOf)
    {
        var today = asOf.Date;
        var planStart = new DateTime(today.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime Ago(int days) => DateTime.SpecifyKind(today.AddDays(-days), DateTimeKind.Utc);
        DateTime Dob(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

        var networks = new List<DemoNetwork>
        {
            new("DEMO-NET-COMM", "Desert Sun Commercial Network", "Commercial"),
            new("DEMO-NET-MCD", "Saguaro Community Care Network", "Medicaid"),
            new("DEMO-NET-MA", "Mesa Gold Advantage Network", "Medicare"),
        };

        var plans = new List<DemoPlan>
        {
            new(CommercialPlan, Guid.Parse("5d3c0c1e-0001-4a51-9d1e-00000000c001"), "DEMO-COMM-PPO",
                "Desert Sun PPO", "Commercial", "PPO", "DEMO-NET-COMM"),
            new(MedicaidPlan, Guid.Parse("5d3c0c1e-0002-4a51-9d1e-00000000c002"), "DEMO-MCD-HMO",
                "Saguaro Community Care", "Medicaid", "HMO", "DEMO-NET-MCD"),
            new(MedicarePlan, Guid.Parse("5d3c0c1e-0003-4a51-9d1e-00000000c003"), "DEMO-MA-HMO",
                "Mesa Gold Advantage", "Medicare", "HMO", "DEMO-NET-MA"),
        };
        var allPlans = plans.Select(p => p.Key).ToList();

        var mesa = new DemoAddress("2150 E Juniper Ridge Dr", "Mesa", "AZ", "85204");
        var providers = new List<DemoProvider>
        {
            new("canyon-ridge", Identifiers.Npi("192837465"), true, null, null, "Canyon Ridge Orthopedics", null,
                "Orthopedic Surgery", "207X00000X", "861234501",
                new("880 W Canyon Ridge Pkwy, Suite 200", "Tempe", "AZ", "85283"), "480-555-0110", allPlans, false),
            new("okafor", Identifiers.Npi("192837466"), false, "Daniel", "Okafor", null, "MD",
                "Orthopedic Surgery", "207X00000X", "861234501",
                new("880 W Canyon Ridge Pkwy, Suite 200", "Tempe", "AZ", "85283"), "480-555-0110", allPlans, false),
            new("holt", Identifiers.Npi("192837467"), false, "Megan", "Holt", null, "PT",
                "Physical Therapy", "225100000X", "861234501",
                new("880 W Canyon Ridge Pkwy, Suite 120", "Tempe", "AZ", "85283"), "480-555-0111", allPlans, false),
            new("rahman", Identifiers.Npi("192837468"), false, "Aisha", "Rahman", null, "MD",
                "Family Medicine", "207Q00000X", "862345602",
                new("415 N Alma School Rd", "Mesa", "AZ", "85201"), "480-555-0120", allPlans, true),
            new("mesa-vista", Identifiers.Npi("192837469"), true, null, null, "Mesa Vista Medical Center", null,
                "Acute Care Hospital", "282N00000X", "863456703",
                new("1200 S Dobson Rd", "Mesa", "AZ", "85202"), "480-555-0130", allPlans, false),
            new("valley-imaging", Identifiers.Npi("192837470"), true, null, null, "Valley Imaging Center", null,
                "Diagnostic Radiology", "261QR0200X", "864567804",
                new("3301 E Baseline Rd", "Gilbert", "AZ", "85234"), "480-555-0140", allPlans, false),
        };

        var contracts = new List<DemoContract>
        {
            new("CTR-DEMO-CRO-COMM", "canyon-ridge", "Commercial", "FeeForService", planStart, planStart.AddYears(3)),
            new("CTR-DEMO-MVMC-MCD", "mesa-vista", "Medicaid", "FeeForService", planStart, today.AddDays(75)),
        };

        var sponsors = new List<DemoSponsor>
        {
            new(DesertSunGroup, "Desert Sun Builders", "865678905",
                new("4400 E Broadway Rd", "Phoenix", "AZ", "85040"), "Teresa Lund", "602-555-0150", 650m, 1, 30),
            new(CopperStateGroup, "Copper State Logistics", "866789006",
                new("7800 W Buckeye Rd", "Phoenix", "AZ", "85043"), "Marcus Bell", "602-555-0160", 610m, 1, 30),
        };

        DemoMember Member(string id, string first, string last, DateTime dob, string gender, string group, string plan,
            string? subscriber, string relationship, DemoAddress address, string phone,
            string? pcp = null, IReadOnlyList<DemoIdentifier>? ids = null, DemoOtherInsurance? other = null,
            decimal premium = 0, decimal employer = 0)
            => new(id, first, last, dob, gender, group, plan, subscriber is null, subscriber, relationship,
                address, phone, ids ?? Array.Empty<DemoIdentifier>(), pcp, other, premium, employer);

        var phoenix = new DemoAddress("3815 W Palo Verde Ave", "Phoenix", "AZ", "85019");
        var tempe = new DemoAddress("96 E Ironwood Ln", "Tempe", "AZ", "85281");
        var chandler = new DemoAddress("511 W Mesquite Trl", "Chandler", "AZ", "85225");
        var glendale = new DemoAddress("6020 N 59th Ave", "Glendale", "AZ", "85301");
        var members = new List<DemoMember>
        {
            // The Delgado family: employer PPO through Desert Sun Builders.
            Member("DSB100101", "Maria", "Delgado", Dob(1984, 3, 14), "F", DesertSunGroup, CommercialPlan,
                null, "18", mesa, "480-555-0142", "rahman", premium: 650m, employer: 455m),
            Member("DSB100102", "Luis", "Delgado", Dob(1982, 11, 2), "M", DesertSunGroup, CommercialPlan,
                "DSB100101", "01", mesa, "480-555-0142", "rahman",
                other: new("Copper Mutual Insurance", "CMI-448120", "CMI-GRP-77")),
            Member("DSB100103", "Sofia", "Delgado", Dob(2014, 6, 21), "F", DesertSunGroup, CommercialPlan,
                "DSB100101", "19", mesa, "480-555-0142", "rahman"),
            // Two similar names, for showing exact vs "sounds like" name search.
            Member("DSB100201", "John", "Smith", Dob(1979, 7, 4), "M", DesertSunGroup, CommercialPlan,
                null, "18", chandler, "480-555-0177", "rahman", premium: 650m, employer: 455m),
            Member("CSL200101", "Jon", "Smyth", Dob(1981, 9, 30), "M", CopperStateGroup, CommercialPlan,
                null, "18", glendale, "623-555-0181", premium: 610m, employer: 427m),
            Member("CSL200201", "Rosa", "Whitfield", Dob(1990, 1, 12), "F", CopperStateGroup, CommercialPlan,
                null, "18", glendale, "623-555-0182", premium: 610m, employer: 427m),
            Member("CSL200301", "Kevin", "Tran", Dob(1987, 5, 8), "M", CopperStateGroup, CommercialPlan,
                null, "18", phoenix, "602-555-0183", premium: 610m, employer: 427m),
            // Medicaid member with a state Medicaid ID on her card.
            Member("MCD200101", "Gloria", "Pham", Dob(1971, 8, 9), "F", MedicaidGroup, MedicaidPlan,
                null, "18", phoenix, "602-555-0191", "rahman",
                ids: new[] { new DemoIdentifier("Medicaid", "A90011234") }),
            // Medicare Advantage member with an MBI. 1EG4TE5MK73 is CMS's published example MBI.
            Member("MA300101", "Harold", "Jenkins", Dob(1949, 2, 17), "M", MedicareGroup, MedicarePlan,
                null, "18", tempe, "480-555-0195", "rahman",
                ids: new[] { new DemoIdentifier("MedicareMbi", "1EG4TE5MK73") }),
        };

        var authorizations = new List<DemoAuthorization>
        {
            // Gloria: admitted three days ago; three days approved so far, the rest under concurrent review.
            new("AUTH-DEMO-1001", "MCD200101", "rahman", "rahman", "mesa-vista", "ConcurrentReview", "U", "48",
                Ago(3), DateTime.SpecifyKind(today.AddDays(2), DateTimeKind.Utc),
                new[] { "J18.9" }, new[] { new DemoService("99223", "Initial hospital care, high complexity", 5) },
                "Approved", 3),
            // Harold: expedited knee MRI request submitted today, decision still open.
            new("AUTH-DEMO-1002", "MA300101", "okafor", "valley-imaging", null, "PreAuthorization", "U", "1",
                DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Utc), DateTime.SpecifyKind(today.AddDays(30), DateTimeKind.Utc),
                new[] { "M23.221" }, new[] { new DemoService("73721", "MRI knee without contrast", 1) },
                null, null),
            // Maria: physical therapy course, 20 visits approved.
            new("AUTH-DEMO-1003", "DSB100101", "okafor", "holt", null, "PreAuthorization", "E", "1",
                Ago(45), DateTime.SpecifyKind(today.AddDays(45), DateTimeKind.Utc),
                new[] { "M75.101" }, new[] { new DemoService("97110", "Therapeutic exercise", 20) },
                "Approved", 20),
        };

        var claims = new List<DemoClaim>
        {
            Pt("CLM-DEMO-1001", "CRO-55821", 35),
            Pt("CLM-DEMO-1002", "CRO-55904", 28),
            Pt("CLM-DEMO-1003", "CRO-56017", 21),
            new("CLM-DEMO-1004", "VIC-30417", "DSB100103", CommercialPlan, "valley-imaging", "valley-imaging", 1, "11",
                Ago(20), Ago(20), new[] { "R51.9", "R42" },
                new[] { new DemoClaimLine("70551", "MRI brain without contrast", 1, 1850m) },
                null, "Advanced imaging billed without a prior authorization: expect a denial or pend; it is appealed below."),
            new("CLM-DEMO-1005", "AR-77310", "DSB100102", CommercialPlan, "rahman", "rahman", 1, "11",
                Ago(18), Ago(18), new[] { "I10" },
                new[] { new DemoClaimLine("99214", "Office visit, established patient", 1, 210m) },
                null, "Luis has other insurance on file: expect a pend for coordination of benefits."),
            new("CLM-DEMO-1006", "CRO-55377", "DSB100201", CommercialPlan, "canyon-ridge", "okafor", 1, "11",
                Ago(25), Ago(25), new[] { "M17.11" },
                new[]
                {
                    new DemoClaimLine("99204", "Office visit, new patient", 1, 340m),
                    new DemoClaimLine("20610", "Arthrocentesis, major joint", 1, 285m),
                },
                null, "Clean claim for Canyon Ridge: expect approval; it is paid in the next payment run."),
            new("CLM-DEMO-1007", "CRO-55420", "CSL200201", CommercialPlan, "canyon-ridge", "okafor", 1, "11",
                Ago(24), Ago(24), new[] { "S83.241A" },
                new[] { new DemoClaimLine("99203", "Office visit, new patient", 1, 260m) },
                null, "Clean claim for Canyon Ridge: expect approval; it is paid in the next payment run."),
            new("CLM-DEMO-1008", "AR-77104", "MCD200101", MedicaidPlan, "rahman", "rahman", 1, "11",
                Ago(40), Ago(40), new[] { "E11.9" },
                new[] { new DemoClaimLine("99213", "Office visit, established patient", 1, 150m) },
                null, "Routine PCP visit before Gloria's admission: expect approval."),
            new("CLM-DEMO-1009", "CRO-56102", "MA300101", MedicarePlan, "canyon-ridge", "okafor", 1, "11",
                Ago(10), Ago(10), new[] { "M23.221" },
                new[] { new DemoClaimLine("99204", "Office visit, new patient", 1, 340m) },
                null, "The visit that led to Harold's MRI request: expect approval."),
        };

        DemoClaim Pt(string number, string account, int daysAgo)
            => new(number, account, "DSB100101", CommercialPlan, "canyon-ridge", "holt", 1, "11",
                Ago(daysAgo), Ago(daysAgo), new[] { "M75.101" },
                new[]
                {
                    new DemoClaimLine("97110", "Therapeutic exercise", 2, 180m),
                    new DemoClaimLine("97140", "Manual therapy", 1, 95m),
                },
                "AUTH-DEMO-1003", "Physical therapy visit under Maria's approved auth: expect approval; counts toward her visit limit.");

        var appeals = new List<DemoAppeal>
        {
            new("APL-DEMO-1001", "CLM-DEMO-1004", "Reconsideration",
                "Valley Imaging requests reconsideration of the brain MRI for Sofia Delgado. The ordering physician " +
                "saw her in the emergency department for persistent headaches with dizziness and ordered the MRI the " +
                "same week; the office did not know prior authorization was required for this plan.",
                false),
        };

        var stories = BuildStories(today, providers);

        return new DemoDataset(asOf, networks, plans, providers, contracts, sponsors, members,
            authorizations, claims, appeals, new[] { CopperStateGroup }, stories);
    }

    private static List<DemoStory> BuildStories(DateTime today, IReadOnlyList<DemoProvider> providers)
    {
        string Npi(string key) => providers.Single(p => p.Key == key).Npi;
        return new List<DemoStory>
        {
            new("A member calls about her family's coverage", "Customer service",
                new[]
                {
                    "Search Members for member ID DSB100101, or last name Delgado with DOB 03/14/1984.",
                    "Open Maria Delgado. Show the family tab: Luis (spouse) and Sofia (daughter).",
                    "Show her PCP (Dr. Aisha Rahman) and her physical therapy authorization AUTH-DEMO-1003: 20 visits approved.",
                    "Open her Claims tab: three PT visits (CLM-DEMO-1001 to 1003) count toward the visit limit.",
                }),
            new("A provider office calls about a denied MRI", "Provider services",
                new[]
                {
                    "The caller is Valley Imaging Center (NPI " + Npi("valley-imaging") + ").",
                    "Search Claims for claim CLM-DEMO-1004, or for member Sofia Delgado (DSB100103).",
                    "Show its status and why: a brain MRI, which needs prior authorization on this plan, billed without one.",
                    "Open the appeal APL-DEMO-1001 the office already filed, and its clock.",
                    "The office only has its own account number, VIC-30417. Claims submitted through POST /api/v1/claims don't store it yet; once they do and patient account search ships (portal-search.md §4.1), search VIC-30417 directly.",
                }),
            new("An expedited authorization is due", "Utilization management",
                new[]
                {
                    "Open Authorizations and search for member MA300101 (Harold Jenkins), or auth AUTH-DEMO-1002.",
                    "It is an urgent (expedited) knee MRI request submitted " + today.ToString("MM/dd/yyyy") + ": the decision clock is running.",
                    "Contrast with AUTH-DEMO-1001: Gloria Pham's inpatient stay at Mesa Vista Medical Center, 3 of 5 days approved, under concurrent review.",
                }),
            new("A claim pends for coordination of benefits", "Claims examiner",
                new[]
                {
                    "Search Claims for CLM-DEMO-1005 (Luis Delgado, office visit with Dr. Rahman).",
                    "Luis has other coverage on file (Copper Mutual Insurance, policy CMI-448120): show how the claim handles COB.",
                }),
            new("A provider group asks where its money is", "Provider services / finance",
                new[]
                {
                    "Canyon Ridge Orthopedics (TIN 86-1234501, NPI " + Npi("canyon-ridge") + ") has approved claims CLM-DEMO-1006 and CLM-DEMO-1007.",
                    "After a payment run, search the check or EFT number to show the payment, its 835 and the claims it paid.",
                    "The seeder does not run the payment run: it needs a trading partner for the payee and a second approver (see README).",
                }),
            new("An employer group's premium", "Premium billing",
                new[]
                {
                    "Open Premium Billing → Invoices and filter by group CSL-2001 (Copper State Logistics).",
                    "Three employees are billed (Jon Smyth, Rosa Whitfield, Kevin Tran).",
                    "The invoice becomes past due after its due date, and delinquent after the 30-day grace period, when delinquency processing runs.",
                }),
            new("Two members with similar names", "Search",
                new[]
                {
                    "Search Members for last name Smith: John Smith (DSB100201) is found.",
                    "Jon Smyth (CSL200101) is not, because the search matches spelling. Once \"sounds like\" ships (portal-search.md §3.3), the same search finds both.",
                }),
        };
    }
}
