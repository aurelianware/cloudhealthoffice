using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates a pool of synthetic providers (individual + organizational) with
/// Luhn-10 valid NPIs, realistic taxonomy codes, and network participation.
/// </summary>
public class SyntheticProviderGenerator
{
    private static readonly string[] MaleFirstNames =
    {
        "James", "Robert", "John", "Michael", "David", "William", "Richard", "Joseph",
        "Thomas", "Charles", "Christopher", "Daniel", "Matthew", "Anthony", "Mark",
        "Steven", "Andrew", "Joshua", "Kenneth", "Kevin",
    };

    private static readonly string[] FemaleFirstNames =
    {
        "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica",
        "Sarah", "Karen", "Lisa", "Nancy", "Betty", "Margaret", "Sandra",
        "Ashley", "Emily", "Donna", "Michelle", "Dorothy",
    };

    private static readonly string[] LastNames =
    {
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
        "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
        "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson",
    };

    private static readonly string[] HospitalNames =
    {
        "Regional Medical Center", "Community Hospital", "General Hospital",
        "Memorial Hospital", "Methodist Hospital", "Baptist Hospital",
        "Presbyterian Hospital", "St. Luke's Hospital", "Baylor Medical Center",
        "Regional County Hospital", "Children's Medical Center",
        "Medical City Hospital", "Texas Health Hospital",
        "JPS Health Network", "Cook Children's Hospital",
    };

    private static readonly string[] ClinicNames =
    {
        "Primary Care Clinic", "Family Health Center", "Community Health Center",
        "Urgent Care Center", "Medical Associates", "Health Partners",
        "Community Medical Group", "Family Medicine Associates",
        "Internal Medicine Associates", "Pediatric Associates",
    };

    private static readonly string[] SnfNames =
    {
        "Healthcare Center", "Rehabilitation Center", "Nursing & Rehabilitation",
        "Senior Care Center", "Extended Care Facility", "Post-Acute Care",
        "Skilled Nursing Center", "Long-Term Care Center",
    };

    private static readonly string[] BhNames =
    {
        "Behavioral Health Center", "Mental Health Services", "Counseling Center",
        "Psychiatric Services", "Behavioral Wellness Center",
    };

    private static readonly string[] StreetNames =
    {
        "Medical District Dr", "Hospital Blvd", "Health Center Way",
        "Physicians Pkwy", "Clinic Dr", "Medical Pkwy", "Healthcare Ln",
        "Preston Rd", "Central Expy", "Harry Hines Blvd",
        "Stemmons Fwy", "Greenville Ave", "Belt Line Rd", "Legacy Dr",
    };

    private static readonly (string City, string[] ZipCodes)[] DfwCities =
    {
        ("Dallas", new[] { "75201", "75204", "75206", "75219", "75220", "75225", "75230", "75231", "75235", "75240", "75243", "75246", "75247" }),
        ("Fort Worth", new[] { "76101", "76102", "76104", "76107", "76109", "76110", "76115", "76116", "76132", "76133", "76137" }),
        ("Arlington", new[] { "76010", "76011", "76012", "76013", "76014", "76015", "76016", "76017" }),
        ("Plano", new[] { "75023", "75024", "75075", "75093" }),
        ("Irving", new[] { "75038", "75039", "75060", "75061", "75062" }),
        ("Garland", new[] { "75040", "75041", "75042", "75043", "75044" }),
        ("Frisco", new[] { "75033", "75034", "75035" }),
        ("Denton", new[] { "76201", "76205", "76210" }),
    };

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntheticProviderGenerator"/> class.
    /// </summary>
    public SyntheticProviderGenerator(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Generate the full provider pool asynchronously.
    /// </summary>
    public Task<List<SyntheticProvider>> GenerateAsync(
        ProviderPoolProfile profile,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Generate(profile), cancellationToken);
    }

    /// <summary>
    /// Generate the full provider pool synchronously.
    /// </summary>
    public List<SyntheticProvider> Generate(ProviderPoolProfile profile)
    {
        var random = new Random(profile.Seed);
        var providers = new List<SyntheticProvider>(
            profile.IndividualProviderCount + profile.OrganizationalProviderCount);

        // Generate individual providers
        for (int i = 1; i <= profile.IndividualProviderCount; i++)
        {
            providers.Add(GenerateIndividualProvider(i, random, profile));

            if (i % 1000 == 0)
            {
                _logger.LogInformation("Generated {Count:N0} / {Total:N0} individual providers",
                    i, profile.IndividualProviderCount);
            }
        }

        // Generate organizational providers — validate that facility counts match the profile total
        var facDist = profile.FacilityDistribution;
        var facilityTotal = facDist.Hospitals + facDist.Clinics + facDist.SkilledNursingFacilities + facDist.BehavioralHealth;
        if (facilityTotal != profile.OrganizationalProviderCount)
        {
            throw new InvalidOperationException(
                $"FacilityDistribution total ({facilityTotal}) does not match " +
                $"OrganizationalProviderCount ({profile.OrganizationalProviderCount}). " +
                $"Hospitals={facDist.Hospitals}, Clinics={facDist.Clinics}, " +
                $"SNFs={facDist.SkilledNursingFacilities}, BehavioralHealth={facDist.BehavioralHealth}.");
        }

        int orgSeq = 0;

        for (int i = 0; i < facDist.Hospitals; i++)
            providers.Add(GenerateOrganizationalProvider(++orgSeq, "Hospital", random, profile));
        for (int i = 0; i < facDist.Clinics; i++)
            providers.Add(GenerateOrganizationalProvider(++orgSeq, "Clinic", random, profile));
        for (int i = 0; i < facDist.SkilledNursingFacilities; i++)
            providers.Add(GenerateOrganizationalProvider(++orgSeq, "SNF", random, profile));
        for (int i = 0; i < facDist.BehavioralHealth; i++)
            providers.Add(GenerateOrganizationalProvider(++orgSeq, "BehavioralHealth", random, profile));

        _logger.LogInformation(
            "Provider generation complete: {Individual:N0} individual + {Org:N0} organizational = {Total:N0} total",
            profile.IndividualProviderCount, orgSeq, providers.Count);

        return providers;
    }

    private SyntheticProvider GenerateIndividualProvider(int sequence, Random random, ProviderPoolProfile profile)
    {
        var taxonomy = TaxonomyCodes.SelectWeighted(TaxonomyCodes.AllIndividual, random);
        var gender = random.Next(2) == 0 ? "M" : "F";
        var (networkStatus, isParticipating) = SelectNetworkStatus(random, profile);
        var credentialingStatus = isParticipating
            ? (random.NextDouble() < 0.90 ? "Active" : "Provisional")
            : "Expired";
        var (city, zip) = SelectDfwAddress(random);
        var streetNum = random.Next(100, 9999);
        var streetName = StreetNames[random.Next(StreetNames.Length)];

        var effectiveDate = DateTime.Today.AddMonths(-random.Next(6, 72));
        DateTime? termDate = networkStatus == "Terminated"
            ? effectiveDate.AddMonths(random.Next(12, 48))
            : (random.NextDouble() < 0.10 ? DateTime.Today.AddMonths(random.Next(1, 12)) : null);

        var contractType = SelectContractType(random, profile.ContractTypes);

        return new SyntheticProvider
        {
            Npi = GenerateLuhnNpi(random),
            TaxId = $"{random.Next(72, 79)}-{random.Next(1_000_000, 9_999_999)}",
            ProviderType = "Individual",
            FirstName = gender == "M"
                ? MaleFirstNames[random.Next(MaleFirstNames.Length)]
                : FemaleFirstNames[random.Next(FemaleFirstNames.Length)],
            LastName = LastNames[random.Next(LastNames.Length)],
            Credentials = taxonomy.Credentials,
            SpecialtyCode = taxonomy.Code,
            SpecialtyDescription = taxonomy.Description,
            TaxonomyCode = taxonomy.Code,
            IsParticipating = isParticipating,
            NetworkStatus = networkStatus,
            CredentialingStatus = credentialingStatus,
            Address = $"{streetNum} {streetName}",
            City = city,
            State = "TX",
            ZipCode = zip,
            Phone = $"({random.Next(200, 999)}) {random.Next(200, 999)}-{random.Next(1000, 9999)}",
            ContractType = contractType,
            FeeScheduleId = isParticipating ? "FS-MEDICAID" : "FS-OON",
            EffectiveDate = effectiveDate,
            TermDate = termDate,
            TenantId = profile.TenantId,
        };
    }

    private SyntheticProvider GenerateOrganizationalProvider(
        int sequence, string facilityType, Random random, ProviderPoolProfile profile)
    {
        var (city, zip) = SelectDfwAddress(random);
        var streetNum = random.Next(100, 9999);
        var streetName = StreetNames[random.Next(StreetNames.Length)];
        var (networkStatus, isParticipating) = SelectNetworkStatus(random, profile);

        var taxonomy = facilityType switch
        {
            "Hospital" => TaxonomyCodes.Facilities.First(f => f.Category == "Hospital"),
            "SNF" => TaxonomyCodes.Facilities.First(f => f.Category == "SNF"),
            "BehavioralHealth" => TaxonomyCodes.Facilities.First(f => f.Category == "BehavioralHealth"),
            _ => TaxonomyCodes.Facilities.First(f => f.Category == "Clinic"),
        };
        // Select a specific taxonomy within the category
        var categoryEntries = TaxonomyCodes.Facilities.Where(f => f.Category == taxonomy.Category).ToArray();
        taxonomy = categoryEntries[random.Next(categoryEntries.Length)];

        var orgName = facilityType switch
        {
            "Hospital" => $"{city} {HospitalNames[random.Next(HospitalNames.Length)]}",
            "Clinic" => $"{city} {ClinicNames[random.Next(ClinicNames.Length)]}",
            "SNF" => $"{city} {SnfNames[random.Next(SnfNames.Length)]}",
            "BehavioralHealth" => $"{city} {BhNames[random.Next(BhNames.Length)]}",
            _ => $"{city} Medical Center",
        };

        var contractType = facilityType switch
        {
            "Hospital" => "FeeForService", // DRG-based
            "SNF" => "PerDiem",
            _ => "FeeForService",
        };

        var effectiveDate = DateTime.Today.AddMonths(-random.Next(12, 72));

        return new SyntheticProvider
        {
            Npi = GenerateLuhnNpi(random),
            TaxId = $"{random.Next(72, 79)}-{random.Next(1_000_000, 9_999_999)}",
            ProviderType = "Organization",
            LastName = orgName,
            OrganizationName = orgName,
            SpecialtyCode = taxonomy.Code,
            SpecialtyDescription = taxonomy.Description,
            TaxonomyCode = taxonomy.Code,
            IsParticipating = isParticipating,
            NetworkStatus = networkStatus,
            CredentialingStatus = isParticipating ? "Active" : "Expired",
            Address = $"{streetNum} {streetName}",
            City = city,
            State = "TX",
            ZipCode = zip,
            Phone = $"({random.Next(200, 999)}) {random.Next(200, 999)}-{random.Next(1000, 9999)}",
            FacilityType = facilityType,
            ContractType = contractType,
            FeeScheduleId = isParticipating ? "FS-MEDICAID" : "FS-OON",
            EffectiveDate = effectiveDate,
            TenantId = profile.TenantId,
        };
    }

    /// <summary>
    /// Generate a valid Luhn-10 NPI. NPI is a 10-digit identifier where the first digit
    /// is always 1 (for Type 1) or 2 (for Type 2), and the full number including the
    /// prefix "80840" passes Luhn-10 validation.
    /// </summary>
    internal static string GenerateLuhnNpi(Random random)
    {
        // Generate 9 random digits after the leading "1"
        var digits = new int[10];
        digits[0] = 1; // Type 1 NPI prefix (works for both individual and org in our synthetic data)
        for (int i = 1; i < 9; i++)
        {
            digits[i] = random.Next(0, 10);
        }

        // Calculate Luhn check digit using the "80840" prefix convention
        // The NPI Luhn validation uses prefix "80840" + 9 digits, then check digit
        var prefixedDigits = new int[] { 8, 0, 8, 4, 0 };
        var allDigits = new int[15];
        Array.Copy(prefixedDigits, 0, allDigits, 0, 5);
        Array.Copy(digits, 0, allDigits, 5, 9);

        // Luhn algorithm: double every other digit from right, sum digits
        int sum = 0;
        for (int i = allDigits.Length - 1; i >= 0; i--)
        {
            int d = allDigits[i];
            // Position from right (0-based), the check digit position is 0
            // Since we're computing the check digit, positions shift by 1
            if ((allDigits.Length - i) % 2 == 0)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }

        digits[9] = (10 - (sum % 10)) % 10;
        return string.Join("", digits);
    }

    /// <summary>
    /// Validate that an NPI passes Luhn-10 check with the "80840" prefix.
    /// </summary>
    public static bool ValidateLuhnNpi(string npi)
    {
        if (npi.Length != 10 || !npi.All(char.IsDigit))
            return false;

        var prefixed = "80840" + npi;
        int sum = 0;
        bool alternate = false;
        for (int i = prefixed.Length - 1; i >= 0; i--)
        {
            int d = prefixed[i] - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private static (string Status, bool IsParticipating) SelectNetworkStatus(
        Random random, ProviderPoolProfile profile)
    {
        ValidateNetworkStatusRates(profile);

        var roll = random.NextDouble();
        if (roll < profile.InNetworkRate)
            return ("InNetwork", true);
        if (roll < profile.InNetworkRate + profile.OutOfNetworkRate)
            return ("OutOfNetwork", false);
        if (roll < profile.InNetworkRate + profile.OutOfNetworkRate + profile.TerminatedRate)
            return ("Terminated", false);
        // Fallback for floating-point edge cases
        return ("Terminated", false);
    }

    private static void ValidateNetworkStatusRates(ProviderPoolProfile profile)
    {
        const double tolerance = 0.000001d;
        var total = profile.InNetworkRate + profile.OutOfNetworkRate + profile.TerminatedRate;

        if (Math.Abs(total - 1.0d) > tolerance)
        {
            throw new InvalidOperationException(
                $"ProviderPoolProfile network status rates must sum to 1.0, but got " +
                $"InNetworkRate={profile.InNetworkRate}, OutOfNetworkRate={profile.OutOfNetworkRate}, " +
                $"TerminatedRate={profile.TerminatedRate} (total={total}).");
        }
    }

    private static string SelectContractType(Random random, ContractTypeDistribution dist)
    {
        var roll = random.NextDouble();
        if (roll < dist.FeeForService)
            return "FeeForService";
        if (roll < dist.FeeForService + dist.Capitation)
            return "Capitation";
        return "PerDiem";
    }

    private static (string City, string ZipCode) SelectDfwAddress(Random random)
    {
        var cityEntry = DfwCities[random.Next(DfwCities.Length)];
        var zip = cityEntry.ZipCodes[random.Next(cityEntry.ZipCodes.Length)];
        return (cityEntry.City, zip);
    }
}
