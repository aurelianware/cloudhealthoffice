using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates a pool of synthetic members (subscribers + dependents) with realistic
/// demographics, coverage records, and PCP assignments for benchmark testing.
/// </summary>
public class SyntheticMemberGenerator
{
    private static readonly string[] MaleFirstNames =
    {
        "James", "Robert", "John", "Michael", "David", "William", "Richard", "Joseph",
        "Thomas", "Charles", "Christopher", "Daniel", "Matthew", "Anthony", "Mark",
        "Steven", "Andrew", "Joshua", "Kenneth", "Kevin", "Brian", "George", "Timothy",
        "Ronald", "Edward", "Jason", "Jeffrey", "Ryan", "Jacob", "Nicholas",
    };

    private static readonly string[] FemaleFirstNames =
    {
        "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica",
        "Sarah", "Karen", "Lisa", "Nancy", "Betty", "Margaret", "Sandra", "Ashley",
        "Emily", "Donna", "Michelle", "Dorothy", "Carol", "Amanda", "Melissa", "Deborah",
        "Stephanie", "Rebecca", "Sharon", "Laura", "Cynthia", "Kathleen",
    };

    private static readonly string[] LastNames =
    {
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
        "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
        "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
        "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen",
        "Hill", "Flores", "Green", "Adams", "Nelson", "Baker", "Hall", "Rivera",
        "Campbell", "Mitchell", "Carter", "Roberts",
    };

    private static readonly string[] StreetNames =
    {
        "Main St", "Oak Ave", "Cedar Ln", "Elm St", "Maple Dr", "Pine St", "Walnut Blvd",
        "Park Ave", "Lake Dr", "Hill Rd", "Valley View", "Sunset Blvd", "Spring Creek Pkwy",
        "Meadow Ln", "Forest Dr", "Prairie View", "Commerce St", "Industrial Blvd",
        "Legacy Dr", "Preston Rd", "Central Expy", "Belt Line Rd", "Greenville Ave",
        "Harry Hines Blvd", "Stemmons Fwy", "Mockingbird Ln", "Lovers Ln", "Northwest Hwy",
    };

    private static readonly (string City, string[] ZipCodes)[] DfwCities =
    {
        ("Dallas", new[] { "75201", "75202", "75204", "75205", "75206", "75207", "75208", "75209", "75210", "75211",
                           "75212", "75214", "75215", "75216", "75217", "75218", "75219", "75220", "75223", "75224",
                           "75225", "75226", "75227", "75228", "75229", "75230", "75231", "75232", "75233", "75234",
                           "75235", "75236", "75237", "75238", "75240", "75241", "75243", "75244", "75246", "75247" }),
        ("Fort Worth", new[] { "76101", "76102", "76103", "76104", "76105", "76106", "76107", "76108", "76109", "76110",
                               "76111", "76112", "76115", "76116", "76117", "76118", "76119", "76120", "76123", "76126",
                               "76129", "76131", "76132", "76133", "76134", "76135", "76137", "76140", "76148", "76155" }),
        ("Arlington", new[] { "76001", "76002", "76006", "76010", "76011", "76012", "76013", "76014", "76015", "76016",
                              "76017", "76018", "76019", "76060" }),
        ("Plano", new[] { "75023", "75024", "75025", "75074", "75075", "75093", "75094" }),
        ("Irving", new[] { "75038", "75039", "75060", "75061", "75062", "75063" }),
        ("Garland", new[] { "75040", "75041", "75042", "75043", "75044", "75045", "75046" }),
        ("Frisco", new[] { "75033", "75034", "75035" }),
        ("McKinney", new[] { "75069", "75070", "75071" }),
        ("Denton", new[] { "76201", "76205", "76207", "76208", "76209", "76210" }),
        ("Richardson", new[] { "75080", "75081", "75082", "75083" }),
        ("Mesquite", new[] { "75149", "75150", "75181" }),
        ("Grand Prairie", new[] { "75050", "75051", "75052", "75053", "75054" }),
    };

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntheticMemberGenerator"/> class.
    /// </summary>
    public SyntheticMemberGenerator(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Generate the full member pool asynchronously with progress reporting.
    /// </summary>
    /// <param name="profile">Member pool generation parameters.</param>
    /// <param name="benefitPlans">Available benefit plans for coverage assignment.</param>
    /// <param name="pcpProviders">PCP providers for PCP assignment on HMO plans.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all generated members (subscribers with nested dependents).</returns>
    public Task<List<SyntheticMember>> GenerateAsync(
        MemberPoolProfile profile,
        List<SyntheticBenefitPlan> benefitPlans,
        List<SyntheticProvider>? pcpProviders = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Generate(profile, benefitPlans, pcpProviders), cancellationToken);
    }

    /// <summary>
    /// Generate the full member pool synchronously.
    /// </summary>
    public List<SyntheticMember> Generate(
        MemberPoolProfile profile,
        List<SyntheticBenefitPlan> benefitPlans,
        List<SyntheticProvider>? pcpProviders = null)
    {
        var random = new Random(profile.Seed);
        var subscribers = new List<SyntheticMember>(profile.SubscriberCount);
        var planLookup = benefitPlans.ToDictionary(p => p.PlanId);

        for (int i = 1; i <= profile.SubscriberCount; i++)
        {
            var subscriber = GenerateSubscriber(i, random, profile, planLookup, pcpProviders);
            subscribers.Add(subscriber);

            if (i % 10_000 == 0)
            {
                _logger.LogInformation("Generated {Count:N0} / {Total:N0} subscribers",
                    i, profile.SubscriberCount);
            }
        }

        var totalMembers = subscribers.Count + subscribers.Sum(s => s.Dependents.Count);
        _logger.LogInformation(
            "Member generation complete: {Subscribers:N0} subscribers, {Total:N0} total members",
            subscribers.Count, totalMembers);

        return subscribers;
    }

    private SyntheticMember GenerateSubscriber(
        int sequence,
        Random random,
        MemberPoolProfile profile,
        Dictionary<string, SyntheticBenefitPlan> planLookup,
        List<SyntheticProvider>? pcpProviders)
    {
        var gender = random.Next(2) == 0 ? "M" : "F";
        var dob = GenerateDateOfBirth(random, profile.AgeDistribution);
        var (city, zipCode) = SelectDfwAddress(random);
        var streetNum = random.Next(100, 9999);
        var streetName = StreetNames[random.Next(StreetNames.Length)];
        var (program, planId) = BenefitPlanTemplates.SelectByLobDistribution(random);
        var isTerminated = random.NextDouble() >= profile.ActiveRate;

        var coverageStart = GenerateCoverageStartDate(random, profile.EarliestCoverageDate, profile.LatestCoverageDate);
        var groupNumber = $"{profile.GroupNumberPrefix}-{(sequence % 100) + 1:D3}";

        var subscriber = new SyntheticMember
        {
            MemberId = $"MCC-MBR-{sequence:D7}",
            SubscriberId = $"MCC-SUB-{sequence:D7}",
            FirstName = gender == "M"
                ? MaleFirstNames[random.Next(MaleFirstNames.Length)]
                : FemaleFirstNames[random.Next(FemaleFirstNames.Length)],
            LastName = LastNames[random.Next(LastNames.Length)],
            DateOfBirth = dob,
            Gender = gender,
            Relationship = "Self",
            RelationshipCode = "18",
            IsSubscriber = true,
            CoverageEffectiveDate = coverageStart,
            CoverageTermDate = isTerminated ? coverageStart.AddMonths(random.Next(1, 24)) : null,
            PlanId = planId,
            EnrollmentStatus = isTerminated ? "Terminated" : "Active",
            MaintenanceTypeCode = "021",
            LineOfBusiness = program,
            GroupNumber = groupNumber,
            Address = $"{streetNum} {streetName}",
            City = city,
            State = "TX",
            ZipCode = zipCode,
            Phone = GeneratePhone(random),
            SSN = $"{random.Next(100, 999):D3}-{random.Next(10, 99):D2}-{random.Next(1000, 9999):D4}",
            TenantId = profile.TenantId,
            EmploymentDate = coverageStart.AddDays(-random.Next(30, 365)),
        };

        // Assign PCP for HMO/gatekeeper plans
        if (pcpProviders != null && pcpProviders.Count > 0 && planLookup.TryGetValue(planId, out var plan) && plan.RequiresPcpReferral)
        {
            var pcp = pcpProviders[random.Next(pcpProviders.Count)];
            subscriber.PcpNpi = pcp.Npi;
            subscriber.PcpName = pcp.FullName;
        }

        // Generate coverage records
        var insuranceLines = SelectInsuranceLines(random, profile.InsuranceLines);
        foreach (var lineCode in insuranceLines)
        {
            var coveragePlanId = lineCode switch
            {
                "DEN" => "PLN-DENTAL-CHIP-001",
                "VIS" => "PLN-VISION-CHIP-001",
                _ => planId,
            };

            subscriber.Coverages.Add(CreateCoverage(subscriber, coveragePlanId, lineCode, "EMP", profile.TenantId));
        }

        // Generate dependents
        var depCount = SelectDependentCount(random, profile.DependentDistribution);
        for (int d = 1; d <= depCount; d++)
        {
            var dependent = GenerateDependent(subscriber, d, random, profile, planLookup, pcpProviders);
            subscriber.Dependents.Add(dependent);
        }

        // Update coverage level based on family composition
        var coverageLevel = depCount switch
        {
            0 => "EMP",
            1 when subscriber.Dependents[0].RelationshipCode == "01" => "ESP",
            _ when subscriber.Dependents.Any(dep => dep.RelationshipCode == "01") => "FAM",
            _ => "ECH",
        };

        foreach (var cov in subscriber.Coverages)
        {
            cov.CoverageLevelCode = coverageLevel;
        }
        foreach (var dep in subscriber.Dependents)
        {
            foreach (var cov in dep.Coverages)
            {
                cov.CoverageLevelCode = coverageLevel;
            }
        }

        return subscriber;
    }

    private SyntheticDependent GenerateDependent(
        SyntheticMember subscriber,
        int depSequence,
        Random random,
        MemberPoolProfile profile,
        Dictionary<string, SyntheticBenefitPlan> planLookup,
        List<SyntheticProvider>? pcpProviders)
    {
        bool isSpouse = depSequence == 1 && random.NextDouble() < 0.65;
        var gender = isSpouse
            ? (subscriber.Gender == "M" ? "F" : "M")
            : (random.Next(2) == 0 ? "M" : "F");

        var dob = isSpouse
            ? subscriber.DateOfBirth.AddYears(random.Next(-5, 6)).AddDays(random.Next(-180, 180))
            : GenerateChildDob(subscriber.DateOfBirth, random);

        var dependent = new SyntheticDependent
        {
            MemberId = $"MCC-MBR-{int.Parse(subscriber.MemberId[8..]):D7}{depSequence:D2}",
            SubscriberMemberId = subscriber.MemberId,
            SubscriberId = subscriber.SubscriberId,
            FirstName = gender == "M"
                ? MaleFirstNames[random.Next(MaleFirstNames.Length)]
                : FemaleFirstNames[random.Next(FemaleFirstNames.Length)],
            LastName = subscriber.LastName,
            DateOfBirth = dob,
            Gender = gender,
            RelationshipCode = isSpouse ? "01" : "19",
            Relationship = isSpouse ? "Spouse" : "Child",
            EnrollmentStatus = subscriber.EnrollmentStatus,
            Address = subscriber.Address,
            City = subscriber.City,
            State = subscriber.State,
            ZipCode = subscriber.ZipCode,
            SSN = $"{random.Next(100, 999):D3}-{random.Next(10, 99):D2}-{random.Next(1000, 9999):D4}",
            TenantId = profile.TenantId,
        };

        // Generate coverage records matching subscriber's coverage
        foreach (var subCov in subscriber.Coverages)
        {
            var depCov = CreateCoverage(
                memberId: dependent.MemberId,
                subscriberId: subscriber.SubscriberId,
                groupNumber: subscriber.GroupNumber,
                planId: subCov.PlanId,
                lineCode: subCov.InsuranceLineCode,
                coverageLevel: subCov.CoverageLevelCode,
                effectiveDate: subCov.EffectiveDate,
                termDate: subCov.TermDate,
                tenantId: profile.TenantId);

            // Assign PCP
            if (pcpProviders != null && pcpProviders.Count > 0 &&
                planLookup.TryGetValue(subCov.PlanId, out var plan) && plan.RequiresPcpReferral)
            {
                var pcp = pcpProviders[random.Next(pcpProviders.Count)];
                depCov.PcpNpi = pcp.Npi;
                depCov.PcpName = pcp.FullName;
            }

            dependent.Coverages.Add(depCov);
        }

        return dependent;
    }

    private SyntheticCoverage CreateCoverage(
        SyntheticMember subscriber,
        string planId,
        string lineCode,
        string coverageLevel,
        string tenantId)
    {
        return CreateCoverage(
            subscriber.MemberId,
            subscriber.SubscriberId,
            subscriber.GroupNumber,
            planId,
            lineCode,
            coverageLevel,
            subscriber.CoverageEffectiveDate,
            subscriber.CoverageTermDate,
            tenantId);
    }

    private static SyntheticCoverage CreateCoverage(
        string memberId,
        string subscriberId,
        string groupNumber,
        string planId,
        string lineCode,
        string coverageLevel,
        DateTime effectiveDate,
        DateTime? termDate,
        string tenantId)
    {
        return new SyntheticCoverage
        {
            TenantId = tenantId,
            MemberId = memberId,
            SubscriberId = subscriberId,
            GroupNumber = groupNumber,
            PlanId = planId,
            InsuranceLineCode = lineCode,
            CoverageLevelCode = coverageLevel,
            EffectiveDate = effectiveDate,
            TermDate = termDate,
            Status = termDate.HasValue ? "Terminated" : "Active",
            LineOfBusiness = "Medicaid",
            MaintenanceTypeCode = "021",
        };
    }

    internal static DateTime GenerateDateOfBirth(Random random, AgeDistribution dist)
    {
        var today = DateTime.Today;
        var roll = random.NextDouble();
        int minAge, maxAge;

        if (roll < dist.Under18)
        {
            minAge = 0; maxAge = 17;
        }
        else if (roll < dist.Under18 + dist.Age18To44)
        {
            minAge = 18; maxAge = 44;
        }
        else if (roll < dist.Under18 + dist.Age18To44 + dist.Age45To64)
        {
            minAge = 45; maxAge = 64;
        }
        else
        {
            minAge = 65; maxAge = 85;
        }

        var age = random.Next(minAge, maxAge + 1);
        var dob = today.AddYears(-age).AddDays(-random.Next(0, 365));
        return dob;
    }

    private static DateTime GenerateChildDob(DateTime subscriberDob, Random random)
    {
        // Children should be realistic ages relative to parent
        var subscriberAge = (DateTime.Today - subscriberDob).Days / 365;

        // If subscriber is too young to realistically have children, generate an infant/toddler
        if (subscriberAge < 20)
        {
            var childAge = random.Next(0, Math.Max(1, subscriberAge));
            return DateTime.Today.AddYears(-childAge).AddDays(-random.Next(0, 365));
        }

        var maxParentAge = Math.Min(35, subscriberAge);
        var parentAgeAtBirth = random.Next(18, maxParentAge + 1);
        var childAge2 = subscriberAge - parentAgeAtBirth;
        if (childAge2 < 0) childAge2 = 0;
        if (childAge2 > 25) childAge2 = random.Next(0, 18);

        return DateTime.Today.AddYears(-childAge2).AddDays(-random.Next(0, 365));
    }

    private static DateTime GenerateCoverageStartDate(Random random, DateTime earliest, DateTime latest)
    {
        var range = (latest - earliest).Days;
        if (range <= 0) return earliest;
        return earliest.AddDays(random.Next(0, range));
    }

    private static (string City, string ZipCode) SelectDfwAddress(Random random)
    {
        var cityEntry = DfwCities[random.Next(DfwCities.Length)];
        var zip = cityEntry.ZipCodes[random.Next(cityEntry.ZipCodes.Length)];
        return (cityEntry.City, zip);
    }

    private static string GeneratePhone(Random random)
    {
        return $"({random.Next(200, 999)}) {random.Next(200, 999)}-{random.Next(1000, 9999)}";
    }

    private static List<string> SelectInsuranceLines(Random random, InsuranceLineDistribution dist)
    {
        var roll = random.NextDouble();
        if (roll < dist.HealthOnly)
            return new List<string> { "HLT" };
        if (roll < dist.HealthOnly + dist.HealthAndDental)
            return new List<string> { "HLT", "DEN" };
        return new List<string> { "HLT", "DEN", "VIS" };
    }

    private static int SelectDependentCount(Random random, DependentDistribution dist)
    {
        var roll = random.NextDouble();
        if (roll < dist.ZeroDependents)
            return 0;
        if (roll < dist.ZeroDependents + dist.OneDependents)
            return 1;
        if (roll < dist.ZeroDependents + dist.OneDependents + dist.TwoThreeDependents)
            return random.Next(2, 4); // 2 or 3
        return random.Next(4, 7); // 4 to 6
    }
}
