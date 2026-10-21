using System.Text.Json;
using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

/// <summary>
/// Generated claims are anchored to a reference ("as of") date: no service,
/// admission, discharge, line or receipt date is after it, and the same seed
/// with the same reference date reproduces the same corpus.
/// </summary>
public class ClaimDateAnchorTests
{
    private static readonly string[] ProfessionalSubTypes =
        ["officevisit", "multiline", "globalsurgery", "bilateral", "assistantsurgeon", "telemedicine", "labpathology"];

    private static readonly string[] InstitutionalSubTypes =
        ["inpatient", "outpatient", "emergency", "observation", "stoploss", "skillednursing"];

    private static readonly string[] DentalSubTypes =
        ["preventive", "restorative", "endodontics", "periodontics", "orthodontics", "oralsurgery"];

    private static readonly string[] EdgeCaseSubTypes = Enum.GetNames<EdgeCaseScenario>();

    public static TheoryData<DateTime> ReferenceDates => new()
    {
        new DateTime(2026, 10, 15),
        new DateTime(2024, 2, 29),
        new DateTime(2025, 1, 1),
    };

    [Theory]
    [MemberData(nameof(ReferenceDates))]
    public void NoGeneratedClaimDateIsAfterTheReferenceDate(DateTime referenceDate)
    {
        var refData = new InMemoryReferenceDataProvider();
        var generators = new (IClaimGenerator Generator, string[] SubTypes)[]
        {
            (new ProfessionalClaimGenerator(refData, referenceDate), ProfessionalSubTypes),
            (new InstitutionalClaimGenerator(refData, referenceDate), InstitutionalSubTypes),
            (new DentalClaimGenerator(refData, referenceDate), DentalSubTypes),
            (new EdgeCaseClaimGenerator(refData, referenceDate), EdgeCaseSubTypes),
        };

        var checkedClaims = 0;
        for (var seed = 0; seed < 150; seed++)
        {
            foreach (var (generator, subTypes) in generators)
            {
                var random = new Random(seed);
                foreach (var subType in subTypes)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var claim = generator.Generate(++checkedClaims, subType, random);
                        AssertNoDateAfter(claim, referenceDate, $"seed {seed} {generator.ClaimType}/{subType}");
                    }
                }
            }
        }

        Assert.True(checkedClaims > 10_000);
    }

    [Fact]
    public void EveryServiceDateIsWithinTheTimelyFilingBudget()
    {
        var referenceDate = new DateTime(2026, 10, 15);
        var refData = new InMemoryReferenceDataProvider();
        var generators = new (IClaimGenerator Generator, string[] SubTypes)[]
        {
            (new ProfessionalClaimGenerator(refData, referenceDate), ProfessionalSubTypes),
            (new InstitutionalClaimGenerator(refData, referenceDate), InstitutionalSubTypes),
            (new DentalClaimGenerator(refData, referenceDate), DentalSubTypes),
            (new EdgeCaseClaimGenerator(refData, referenceDate), EdgeCaseSubTypes),
        };

        var oldest = referenceDate;
        var id = 0;
        for (var seed = 0; seed < 150; seed++)
        {
            foreach (var (generator, subTypes) in generators)
            {
                var random = new Random(seed);
                foreach (var subType in subTypes)
                {
                    var claim = generator.Generate(++id, subType, random);
                    oldest = new[] { oldest, claim.DateOfService, claim.Lines.Min(l => l.ServiceDate) }.Min();
                }
            }
        }

        // Inside a 365-day filing limit with two months to spare for submission.
        Assert.True((referenceDate - oldest).TotalDays <= ClaimDateAnchor.MaxServiceAgeDays,
            $"oldest service date {oldest:d} is more than {ClaimDateAnchor.MaxServiceAgeDays} days before {referenceDate:d}");
    }

    [Fact]
    public void LongestInstitutionalStay_StillEndsAndIsReceivedOnOrBeforeTheReferenceDate()
    {
        var referenceDate = new DateTime(2026, 10, 15);
        var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider(), referenceDate);

        var latest = DateTime.MinValue;
        for (var seed = 0; seed < 2_000; seed++)
        {
            var claim = generator.Generate(seed, "skillednursing", new Random(seed));
            latest = new[] { latest, claim.DateReceived, claim.Lines.Max(l => l.ServiceEndDate ?? l.ServiceDate) }.Max();
        }

        Assert.True(latest <= referenceDate, $"latest generated date {latest:d} is after {referenceDate:d}");
        // The window is not needlessly far back: long stays get close to the anchor.
        Assert.True(latest >= referenceDate.AddDays(-30), $"latest generated date {latest:d} is far from {referenceDate:d}");
    }

    [Fact]
    public void DefaultReferenceDate_IsTodayUtc()
    {
        var before = DateTime.UtcNow.Date;
        var generator = new ProfessionalClaimGenerator(new InMemoryReferenceDataProvider());
        var after = DateTime.UtcNow.Date;

        Assert.InRange(generator.ReferenceDate, before, after);
        var claim = generator.Generate(1, "officevisit", new Random(7));
        AssertNoDateAfter(claim, after, "default reference date");
    }

    [Fact]
    public async Task SameSeedAndReferenceDate_ReproduceTheCorpus_AndAllDatesAreAnchored()
    {
        var referenceDate = new DateTime(2026, 10, 15);

        var first = await GenerateCorpusAsync(seed: 42, referenceDate);
        var second = await GenerateCorpusAsync(seed: 42, referenceDate);

        Assert.Equal(referenceDate, first.Result.ReferenceDate);
        Assert.Equal(Serialize(first.Claims), Serialize(second.Claims));
        foreach (var claim in first.Claims)
        {
            AssertNoDateAfter(claim, referenceDate, claim.ClaimId);
        }
    }

    [Fact]
    public async Task ShiftingTheReferenceDate_ShiftsClaimDatesOnly()
    {
        var a = await GenerateCorpusAsync(seed: 42, new DateTime(2026, 10, 15));
        var b = await GenerateCorpusAsync(seed: 42, new DateTime(2026, 10, 25));

        Assert.Equal(a.Claims.Count, b.Claims.Count);
        for (var i = 0; i < a.Claims.Count; i++)
        {
            Assert.Equal(a.Claims[i].ClaimId, b.Claims[i].ClaimId);
            Assert.Equal(a.Claims[i].TotalCharges, b.Claims[i].TotalCharges);
            Assert.Equal(10, (b.Claims[i].DateOfService - a.Claims[i].DateOfService).Days);
            Assert.Equal(10, (b.Claims[i].DateReceived - a.Claims[i].DateReceived).Days);
        }
    }

    private static void AssertNoDateAfter(SyntheticClaim claim, DateTime referenceDate, string context)
    {
        var dates = new List<(string Name, DateTime Value)>
        {
            ("DateOfService", claim.DateOfService),
            ("DateReceived", claim.DateReceived),
        };
        if (claim.AccidentDate is { } accident) dates.Add(("AccidentDate", accident));
        if (claim.EdgeCase is EdgeCaseScenario.NewbornAutoAdjudication
            or EdgeCaseScenario.NewbornMotherClaimLink
            or EdgeCaseScenario.NewbornFirstThirtyDays)
        {
            dates.Add(("Member.DateOfBirth", claim.Member.DateOfBirth));
        }
        foreach (var line in claim.Lines)
        {
            dates.Add(($"Line{line.LineNumber}.ServiceDate", line.ServiceDate));
            if (line.ServiceEndDate is { } end) dates.Add(($"Line{line.LineNumber}.ServiceEndDate", end));
        }

        foreach (var (name, value) in dates)
        {
            Assert.True(value.Date <= referenceDate.Date,
                $"{context}: {name} {value:yyyy-MM-dd} is after the reference date {referenceDate:yyyy-MM-dd}");
        }

        Assert.True(claim.DateOfService <= claim.DateReceived, $"{context}: received before service");
    }

    private static async Task<(CorpusResult Result, List<SyntheticClaim> Claims)> GenerateCorpusAsync(
        int seed, DateTime referenceDate)
    {
        var profile = DefaultCorpusProfile.Create(seed: seed);
        Scale(profile, 2_000);
        profile.ReferenceDate = referenceDate;

        var writer = new CollectingWriter();
        var result = await new ClaimCorpusGenerator(new InMemoryReferenceDataProvider())
            .GenerateCorpusAsync(profile, writer);
        var claims = writer.Claims.OrderBy(c => c.ClaimId, StringComparer.Ordinal).ToList();
        return (result, claims);
    }

    private static void Scale(CorpusProfile profile, int total)
    {
        var factor = (double)total / profile.TotalClaims;
        profile.TotalClaims = total;
        profile.Professional.Count = (int)(profile.Professional.Count * factor);
        profile.Institutional.Count = (int)(profile.Institutional.Count * factor);
        profile.Dental.Count = (int)(profile.Dental.Count * factor);
        var e = profile.EdgeCases;
        e.CobCount = (int)Math.Ceiling(e.CobCount * factor);
        e.RetroEligibilityCount = (int)Math.Ceiling(e.RetroEligibilityCount * factor);
        e.NewbornCount = (int)Math.Ceiling(e.NewbornCount * factor);
        e.PriorAuthCount = (int)Math.Ceiling(e.PriorAuthCount * factor);
        e.SubrogationCount = (int)Math.Ceiling(e.SubrogationCount * factor);
        e.BehavioralHealthCount = (int)Math.Ceiling(e.BehavioralHealthCount * factor);
        e.MedicaidCount = (int)Math.Ceiling(e.MedicaidCount * factor);
        e.Count = e.CobCount + e.RetroEligibilityCount + e.NewbornCount + e.PriorAuthCount
                  + e.SubrogationCount + e.BehavioralHealthCount + e.MedicaidCount;
    }

    private static string Serialize(List<SyntheticClaim> claims) => JsonSerializer.Serialize(claims);

    private sealed class CollectingWriter : ICorpusWriter
    {
        private readonly object _gate = new();
        public List<SyntheticClaim> Claims { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WriteClaimAsync(SyntheticClaim claim, CancellationToken cancellationToken = default)
        {
            lock (_gate) Claims.Add(claim);
            return Task.CompletedTask;
        }

        public Task FinalizeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
