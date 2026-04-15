using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class SyntheticProviderGeneratorTests
{
    private readonly SyntheticProviderGenerator _generator;
    private readonly ProviderPoolProfile _smallProfile;

    public SyntheticProviderGeneratorTests()
    {
        _generator = new SyntheticProviderGenerator();
        _smallProfile = new ProviderPoolProfile
        {
            IndividualProviderCount = 100,
            OrganizationalProviderCount = 20,
            Seed = 42,
            TenantId = "test-tenant",
            FacilityDistribution = new FacilityDistribution
            {
                Hospitals = 8,
                Clinics = 6,
                SkilledNursingFacilities = 4,
                BehavioralHealth = 2,
            },
        };
    }

    [Fact]
    public void Generate_ProducesCorrectProviderCounts()
    {
        var providers = _generator.Generate(_smallProfile);

        var individual = providers.Count(p => p.ProviderType == "Individual");
        var organizational = providers.Count(p => p.ProviderType == "Organization");

        Assert.Equal(100, individual);
        Assert.Equal(20, organizational);
        Assert.Equal(120, providers.Count);
    }

    [Fact]
    public void Generate_AllNpisAreLuhn10Valid()
    {
        var providers = _generator.Generate(_smallProfile);

        foreach (var p in providers)
        {
            Assert.Equal(10, p.Npi.Length);
            Assert.True(SyntheticProviderGenerator.ValidateLuhnNpi(p.Npi),
                $"NPI {p.Npi} failed Luhn-10 validation");
        }
    }

    [Fact]
    public void Generate_AllNpisAreUnique()
    {
        var providers = _generator.Generate(_smallProfile);
        var npis = providers.Select(p => p.Npi).ToList();
        Assert.Equal(npis.Count, npis.Distinct().Count());
    }

    [Fact]
    public void Generate_IndividualProvidersHaveNames()
    {
        var providers = _generator.Generate(_smallProfile);

        foreach (var p in providers.Where(p => p.ProviderType == "Individual"))
        {
            Assert.NotEmpty(p.FirstName);
            Assert.NotEmpty(p.LastName);
            Assert.NotEmpty(p.SpecialtyCode);
            Assert.NotEmpty(p.TaxonomyCode);
        }
    }

    [Fact]
    public void Generate_OrganizationalProvidersHaveOrganizationNames()
    {
        var providers = _generator.Generate(_smallProfile);

        foreach (var p in providers.Where(p => p.ProviderType == "Organization"))
        {
            Assert.NotNull(p.OrganizationName);
            Assert.NotEmpty(p.OrganizationName);
            Assert.NotNull(p.FacilityType);
        }
    }

    [Fact]
    public void Generate_AllProvidersHaveTexasAddresses()
    {
        var providers = _generator.Generate(_smallProfile);

        foreach (var p in providers)
        {
            Assert.Equal("TX", p.State);
            Assert.NotEmpty(p.City);
            Assert.NotEmpty(p.ZipCode);
        }
    }

    [Fact]
    public void Generate_NetworkStatusDistribution()
    {
        var profile = new ProviderPoolProfile
        {
            IndividualProviderCount = 500,
            OrganizationalProviderCount = 0,
            Seed = 42,
            InNetworkRate = 0.80,
            OutOfNetworkRate = 0.10,
            TerminatedRate = 0.10,
            FacilityDistribution = new FacilityDistribution
            {
                Hospitals = 0, Clinics = 0,
                SkilledNursingFacilities = 0, BehavioralHealth = 0,
            },
        };
        var providers = _generator.Generate(profile);

        var inNetwork = providers.Count(p => p.NetworkStatus == "InNetwork");
        var outOfNetwork = providers.Count(p => p.NetworkStatus == "OutOfNetwork");
        var terminated = providers.Count(p => p.NetworkStatus == "Terminated");

        // Allow ±10% variance
        Assert.InRange(inNetwork, 350, 450);      // expected ~400
        Assert.InRange(outOfNetwork, 20, 80);      // expected ~50
        Assert.InRange(terminated, 20, 80);        // expected ~50
    }

    [Fact]
    public void Generate_InNetworkProvidersHaveMedicaidFeeSchedule()
    {
        var providers = _generator.Generate(_smallProfile);

        foreach (var p in providers.Where(p => p.IsParticipating))
        {
            Assert.Equal("FS-MEDICAID", p.FeeScheduleId);
        }
    }

    [Fact]
    public void Generate_DeterministicWithSameSeed()
    {
        var providers1 = _generator.Generate(_smallProfile);
        var providers2 = _generator.Generate(_smallProfile);

        Assert.Equal(providers1.Count, providers2.Count);
        for (int i = 0; i < providers1.Count; i++)
        {
            Assert.Equal(providers1[i].Npi, providers2[i].Npi);
            Assert.Equal(providers1[i].FirstName, providers2[i].FirstName);
            Assert.Equal(providers1[i].SpecialtyCode, providers2[i].SpecialtyCode);
        }
    }

    [Fact]
    public void ValidateLuhnNpi_ValidNpi_ReturnsTrue()
    {
        // Known valid NPI: 1234567893 (passes Luhn-10 with 80840 prefix)
        // Generate one and verify
        var random = new Random(42);
        var npi = SyntheticProviderGenerator.GenerateLuhnNpi(random);
        Assert.True(SyntheticProviderGenerator.ValidateLuhnNpi(npi));
    }

    [Fact]
    public void ValidateLuhnNpi_InvalidNpi_ReturnsFalse()
    {
        Assert.False(SyntheticProviderGenerator.ValidateLuhnNpi("1234567890"));
        Assert.False(SyntheticProviderGenerator.ValidateLuhnNpi("123456789")); // too short
        Assert.False(SyntheticProviderGenerator.ValidateLuhnNpi("12345678901")); // too long
        Assert.False(SyntheticProviderGenerator.ValidateLuhnNpi("abcdefghij")); // non-digits
    }

    [Fact]
    public void Generate_FacilityTypesMatchDistribution()
    {
        var providers = _generator.Generate(_smallProfile);
        var orgs = providers.Where(p => p.ProviderType == "Organization").ToList();

        var hospitals = orgs.Count(p => p.FacilityType == "Hospital");
        var clinics = orgs.Count(p => p.FacilityType == "Clinic");
        var snfs = orgs.Count(p => p.FacilityType == "SNF");
        var bh = orgs.Count(p => p.FacilityType == "BehavioralHealth");

        Assert.Equal(8, hospitals);
        Assert.Equal(6, clinics);
        Assert.Equal(4, snfs);
        Assert.Equal(2, bh);
    }
}
