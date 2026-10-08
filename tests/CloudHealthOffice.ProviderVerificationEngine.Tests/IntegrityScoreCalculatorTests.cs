using CloudHealthOffice.ProviderVerificationEngine.Models;
using CloudHealthOffice.ProviderVerificationEngine.Scoring;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests;

public class IntegrityScoreCalculatorTests
{
    private readonly IntegrityScoreCalculator _calculator;

    public IntegrityScoreCalculatorTests()
    {
        var weights = Options.Create(new ScoringWeights
        {
            NpiValidation = 30,
            ExclusionScreening = 30,
            MedicareEnrollment = 15,
            LicenseVerification = 15,
            ConflictOfInterest = 10
        });
        var options = Options.Create(new VerificationOptions());
        _calculator = new IntegrityScoreCalculator(weights, options);
    }

    [Fact]
    public void ExcludedProvider_ReturnsBlockedRating_ScoreZero()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = true,
                Source = ExclusionScreeningSource.OigLeie,
                Matches =
                [
                    new ExclusionMatch
                    {
                        Source = ExclusionScreeningSource.OigLeie,
                        ExcludedName = "Test Provider",
                        MatchConfidence = 1.0f
                    }
                ]
            }
        };

        var score = _calculator.Calculate(record);

        Assert.Equal(0, score.CompositeScore);
        Assert.Equal(IntegrityRating.Blocked, score.Rating);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUDED" && f.Severity == IntegrityFlagSeverity.Blocking);
    }

    [Fact]
    public void ActiveNpi_NoPecosNoFsmb_ReturnsAdvisoryRating()
    {
        // Active NPI with possible exclusion match and no PECOS or FSMB data.
        // NPI: 100 - 10 (no taxonomy) - 10 (no location) = 80, weight 30
        // Exclusion: 40 (possible match), weight 30
        // Composite = (80*30 + 40*30) / 60 = 3600/60 = 60 -> Advisory
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = new NppesProviderData
            {
                Npi = "1234567893",
                NpiStatus = NppesNpiStatus.Active,
                Taxonomies = [], // No primary taxonomy -> -10
                Addresses = []  // No practice location -> -10
            },
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie,
                Matches =
                [
                    new ExclusionMatch { MatchConfidence = 0.75f, Source = ExclusionScreeningSource.OigLeie }
                ]
            },
        };

        var score = _calculator.Calculate(record);

        Assert.Equal(IntegrityRating.Advisory, score.Rating);
        Assert.True(score.CompositeScore >= 60 && score.CompositeScore <= 79,
            $"Expected Advisory range (60-79), got {score.CompositeScore}");
        Assert.Contains(score.Flags, f => f.Code == "NO_PRIMARY_TAXONOMY");
        Assert.Contains(score.Flags, f => f.Code == "NO_PRACTICE_LOCATION");
        Assert.Contains(score.Flags, f => f.Code == "POSSIBLE_EXCLUSION_MATCH");
    }

    [Fact]
    public void ActiveNpi_ClearExclusion_PecosEnrolled_ReturnsClearRating()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie
            },
            PecosStatus = new PecosEnrollmentStatus
            {
                IsEnrolledInMedicare = true,
                ProviderTypeDescription = "Physician"
            }
        };

        var score = _calculator.Calculate(record);

        Assert.True(score.CompositeScore >= 80);
        Assert.Equal(IntegrityRating.Clear, score.Rating);
    }

    [Fact]
    public void DeactivatedNpi_ReturnsCriticalFlag()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = new NppesProviderData
            {
                Npi = "1234567893",
                NpiStatus = NppesNpiStatus.Deactivated,
                DeactivationDate = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
                Taxonomies = [new NppesTaxonomy { Code = "207Q00000X", IsPrimary = true }],
                Addresses = [new NppesAddress { AddressPurpose = "LOCATION" }]
            },
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie
            }
        };

        var score = _calculator.Calculate(record);

        Assert.Contains(score.Flags, f => f.Code == "NPI_DEACTIVATED" && f.Severity == IntegrityFlagSeverity.Critical);
        // Deactivated NPI scores 0 on the NPI dimension
        Assert.Equal(0, score.NpiValidation.Score);
    }

    [Fact]
    public void HighOpenPayments_ReturnsConflictWarning()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie
            },
            OpenPaymentsSummary = new OpenPaymentsSummary
            {
                ProgramYear = 2024,
                TotalGeneralPayments = 150_000m,
                GeneralPaymentCount = 50,
                TotalResearchPayments = 0m,
                HasOwnershipInterest = false
            }
        };

        var score = _calculator.Calculate(record);

        Assert.Contains(score.Flags, f => f.Code == "HIGH_PAYMENTS" && f.Severity == IntegrityFlagSeverity.Warning);
        Assert.True(score.ConflictOfInterest.WasEvaluated);
        Assert.Equal(50, score.ConflictOfInterest.Score);
    }

    [Fact]
    public void ExclusionNotScreened_PlaceholderResult_NotEvaluated_FlaggedAndExcludedFromComposite()
    {
        // Placeholder adapter shape: a result object exists but no real
        // LEIE/SAM source was queried (WasScreened defaults to false).
        // NPI: 100 (weight 30), PECOS not enrolled: 60 (weight 15)
        // Composite = (100*30 + 60*15) / 45 = 86.67 -> 87.
        // Had exclusion been awarded 100 at weight 30 it would be 92.
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                Source = ExclusionScreeningSource.OigLeie
            },
            PecosStatus = new PecosEnrollmentStatus { IsEnrolledInMedicare = false }
        };

        var score = _calculator.Calculate(record);

        Assert.False(score.ExclusionScreening.WasEvaluated);
        Assert.NotEqual(100, score.ExclusionScreening.Score);
        Assert.DoesNotContain("Clear", score.ExclusionScreening.Detail);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED" && f.Severity == IntegrityFlagSeverity.Warning);
        Assert.Equal(87, score.CompositeScore);
        Assert.Equal(IntegrityRating.Unknown, score.Rating);
    }

    [Fact]
    public void ExclusionNotPerformed_NullResult_NotEvaluated_FlaggedRatingUnknown()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = null
        };

        var score = _calculator.Calculate(record);

        Assert.False(score.ExclusionScreening.WasEvaluated);
        Assert.Contains(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
        Assert.Equal(IntegrityRating.Unknown, score.Rating);
    }

    [Fact]
    public void ExclusionNotScreened_NpiNotFound_RatesUnknown_NotBlocked()
    {
        // Composite is 0 (only the NPI dimension evaluated, and it failed),
        // which would bucket as Blocked. Blocked is read downstream as a
        // federal exclusion, which was never found -- so the rating must be
        // Unknown (manual review), not Blocked (B7 denial).
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = null,
            ExclusionScreening = new ExclusionScreeningResult { Source = ExclusionScreeningSource.OigLeie }
        };

        var score = _calculator.Calculate(record);

        Assert.Equal(0, score.CompositeScore);
        Assert.Equal(IntegrityRating.Unknown, score.Rating);
        Assert.Contains(score.Flags, f => f.Code == "NPI_NOT_FOUND");
        Assert.Contains(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
        Assert.DoesNotContain(score.Flags, f => f.Code == "EXCLUDED");
    }

    [Fact]
    public void ExclusionScreenedClear_Scores100_NoNotScreenedFlag()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie
            }
        };

        var score = _calculator.Calculate(record);

        Assert.True(score.ExclusionScreening.WasEvaluated);
        Assert.Equal(100, score.ExclusionScreening.Score);
        Assert.StartsWith("Clear", score.ExclusionScreening.Detail);
        Assert.DoesNotContain(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
        Assert.Equal(IntegrityRating.Clear, score.Rating);
    }

    [Fact]
    public void ExclusionScreened_PossibleMatch_Scores40()
    {
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                WasScreened = true,
                IsExcluded = false,
                Source = ExclusionScreeningSource.OigLeie,
                Matches = [new ExclusionMatch { MatchConfidence = 0.8f, Source = ExclusionScreeningSource.OigLeie }]
            }
        };

        var score = _calculator.Calculate(record);

        Assert.True(score.ExclusionScreening.WasEvaluated);
        Assert.Equal(40, score.ExclusionScreening.Score);
        Assert.Contains(score.Flags, f => f.Code == "POSSIBLE_EXCLUSION_MATCH");
        Assert.DoesNotContain(score.Flags, f => f.Code == "EXCLUSION_NOT_SCREENED");
    }

    [Fact]
    public void ExcludedProvider_HardStopApplies_EvenWithoutScreenedFlag()
    {
        // A confirmed exclusion is never downgraded to "not screened".
        var record = new ProviderVerificationRecord
        {
            Npi = "1234567893",
            NppesData = CreateActiveNppesData(),
            ExclusionScreening = new ExclusionScreeningResult
            {
                IsExcluded = true,
                Source = ExclusionScreeningSource.OigLeie
            }
        };

        var score = _calculator.Calculate(record);

        Assert.Equal(0, score.CompositeScore);
        Assert.Equal(IntegrityRating.Blocked, score.Rating);
    }

    private static NppesProviderData CreateActiveNppesData() => new()
    {
        Npi = "1234567893",
        NpiStatus = NppesNpiStatus.Active,
        EnumerationDate = new DateTimeOffset(2010, 5, 1, 0, 0, 0, TimeSpan.Zero),
        Taxonomies =
        [
            new NppesTaxonomy { Code = "207Q00000X", Description = "Family Medicine", IsPrimary = true }
        ],
        Addresses =
        [
            new NppesAddress { AddressPurpose = "LOCATION", City = "Austin", State = "TX" }
        ]
    };
}
