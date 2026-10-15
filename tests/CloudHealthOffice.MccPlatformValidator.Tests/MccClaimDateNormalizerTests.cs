using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;
using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public class MccClaimDateNormalizerTests
{
    [Theory]
    [InlineData("NewbornAutoAdjudication")]
    [InlineData("NewbornMotherClaimLink")]
    [InlineData("NewbornFirstThirtyDays")]
    public void NormalizeClaimDates_PreservesNewbornAgeAtServiceDate(string scenario)
    {
        // A generator reference date well before today, so the normalized
        // service date (weeks before today) can never coincide with it.
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider(), new DateTime(2025, 1, 1));
        var claim = generator.Generate(1, scenario, new Random(42));
        var originalServiceDate = claim.DateOfService.Date;
        var originalAgeAtServiceDays = (claim.DateOfService.Date - claim.Member.DateOfBirth.Date).Days;

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.NotEqual(originalServiceDate, claim.DateOfService.Date);
        Assert.Equal(originalAgeAtServiceDays, (claim.DateOfService.Date - claim.Member.DateOfBirth.Date).Days);
        Assert.InRange(originalAgeAtServiceDays, 0, 30);
    }

    // On the GKE demo, long institutional stays normalized to "today - 45 +
    // up to 30 days" ran their discharge and receipt dates weeks into the
    // future. Every normalized date must be on or before the reference date,
    // whatever the stay length, for every seed.
    [Fact]
    public void NormalizeClaimDates_NoServiceOrReceiptDateAfterReferenceDate()
    {
        var referenceDate = new DateTime(2026, 10, 15);
        var refData = new InMemoryReferenceDataProvider();
        var institutional = new InstitutionalClaimGenerator(refData, referenceDate);
        var professional = new ProfessionalClaimGenerator(refData, referenceDate);
        var edge = new EdgeCaseClaimGenerator(refData, referenceDate);

        for (var seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            var claims = new List<SyntheticClaim>();
            foreach (var subType in new[] { "skillednursing", "stoploss", "inpatient", "observation", "emergency" })
                claims.Add(institutional.Generate(claims.Count + 1, subType, random));
            claims.Add(professional.Generate(claims.Count + 1, "multiline", random));
            foreach (var scenario in Enum.GetNames<EdgeCaseScenario>())
                claims.Add(edge.Generate(claims.Count + 1, scenario, random));
            var stays = claims.Select(c => (c.Lines.Max(l => l.ServiceEndDate ?? l.ServiceDate) - c.DateOfService).Days).ToList();

            MccClaimDateNormalizer.NormalizeClaimDates(claims, seed, referenceDate);

            for (var i = 0; i < claims.Count; i++)
            {
                var claim = claims[i];
                var latestService = claim.Lines.Max(l => l.ServiceEndDate ?? l.ServiceDate);
                Assert.True(latestService <= referenceDate, $"seed {seed} {claim.ClaimId}: service to {latestService:d}");
                Assert.True(claim.DateReceived <= referenceDate, $"seed {seed} {claim.ClaimId}: received {claim.DateReceived:d}");
                Assert.True(claim.DateReceived > latestService, $"seed {seed} {claim.ClaimId}: received before service ended");
                // The stay itself is preserved, not truncated.
                Assert.Equal(stays[i], (latestService - claim.DateOfService).Days);
            }
        }
    }

    [Fact]
    public void NormalizeClaimDates_SameSeedAndReferenceDate_IsDeterministic()
    {
        var referenceDate = new DateTime(2026, 10, 15);
        List<SyntheticClaim> Build()
        {
            var generator = new InstitutionalClaimGenerator(new InMemoryReferenceDataProvider(), referenceDate);
            var random = new Random(9);
            var claims = Enumerable.Range(1, 20).Select(i => generator.Generate(i, "skillednursing", random)).ToList();
            MccClaimDateNormalizer.NormalizeClaimDates(claims, seed: 9, referenceDate);
            return claims;
        }

        var a = Build();
        var b = Build();
        Assert.Equal(a.Select(c => (c.DateOfService, c.DateReceived)), b.Select(c => (c.DateOfService, c.DateReceived)));
    }

    [Fact]
    public void NormalizeClaimDates_DoesNotShiftDateOfBirthForNonNewbornEdgeCases()
    {
        var generator = new EdgeCaseClaimGenerator(new InMemoryReferenceDataProvider());
        var claim = generator.Generate(1, "CobPrimaryPayer", new Random(42));
        var originalDateOfBirth = claim.Member.DateOfBirth.Date;

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.Equal(originalDateOfBirth, claim.Member.DateOfBirth.Date);
    }

    // Confirmed live at 50K scale: a member ended up with CoverageEffectiveDate
    // landing after their own claim's DateOfService, denying CARC_27 against
    // COB scenarios that never intended to test that boundary. The base
    // generator (EdgeCaseClaimGenerator.Generate) draws member and claim dates
    // from non-overlapping fixed base years, so it can't reproduce the bug
    // directly -- whatever upstream path produced the live failure, the fix
    // that matters is the normalizer's own safety net: given a member already
    // in a bad state (effective after service, as InMemoryReferenceDataProvider's
    // uncorrelated default can produce once shifted through the full pipeline),
    // NormalizeClaimDates must correct it rather than passing it through.
    [Fact]
    public void NormalizeClaimDates_CorrectsMemberEffectiveDateThatWouldLandAfterServiceDate()
    {
        var claim = new SyntheticClaim
        {
            ClaimId = "MCC-E-0000001",
            ClaimType = "EdgeCase",
            EdgeCase = EdgeCaseScenario.CobSecondaryPayer,
            DateOfService = new DateTime(2024, 6, 7),
            DateReceived = new DateTime(2024, 6, 20),
            Member = new SyntheticMember
            {
                MemberId = "MBR-0000001",
                SubscriberId = "MBR-0000001",
                DateOfBirth = new DateTime(1990, 1, 1),
                // Deliberately after DateOfService -- reproduces the bad state
                // a member can be left in before this fix.
                CoverageEffectiveDate = new DateTime(2024, 6, 21),
            },
            Lines = new List<ClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ServiceDate = new DateTime(2024, 6, 7) },
            },
        };

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.True(
            claim.Member.CoverageEffectiveDate.Date <= claim.DateOfService.Date,
            $"member effective {claim.Member.CoverageEffectiveDate:d} is still after service date {claim.DateOfService:d}");
    }

    [Fact]
    public void NormalizeClaimDates_LeavesAlreadyValidMemberEffectiveDateUntouchedBeyondTheShift()
    {
        var claim = new SyntheticClaim
        {
            ClaimId = "MCC-E-0000002",
            ClaimType = "EdgeCase",
            EdgeCase = EdgeCaseScenario.CobSecondaryPayer,
            DateOfService = new DateTime(2024, 6, 7),
            DateReceived = new DateTime(2024, 6, 20),
            Member = new SyntheticMember
            {
                MemberId = "MBR-0000002",
                SubscriberId = "MBR-0000002",
                DateOfBirth = new DateTime(1990, 1, 1),
                CoverageEffectiveDate = new DateTime(2023, 6, 7),
            },
            Lines = new List<ClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213", ServiceDate = new DateTime(2024, 6, 7) },
            },
        };
        var originalGapDays = (claim.DateOfService.Date - claim.Member.CoverageEffectiveDate.Date).Days;

        MccClaimDateNormalizer.NormalizeClaimDates([claim], seed: 42);

        Assert.Equal(originalGapDays, (claim.DateOfService.Date - claim.Member.CoverageEffectiveDate.Date).Days);
    }
}
