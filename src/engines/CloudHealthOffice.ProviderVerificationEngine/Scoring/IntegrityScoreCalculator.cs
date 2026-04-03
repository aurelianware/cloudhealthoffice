namespace CloudHealthOffice.ProviderVerificationEngine.Scoring;

using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Options;

/// <summary>
/// Calculates the composite Provider Integrity Score from all available
/// verification data. Weights are configurable per deployment.
///
/// Scoring philosophy:
///   - Hard stops (exclusion, deactivated NPI) override everything → 0
///   - Each dimension scored 0–100 independently
///   - Composite = weighted average of evaluated dimensions
///   - Flags are additive annotations, not score modifiers
/// </summary>
public class IntegrityScoreCalculator
{
    private readonly ScoringWeights _weights;
    private readonly VerificationOptions _options;

    public IntegrityScoreCalculator(IOptions<ScoringWeights> weights, IOptions<VerificationOptions> options)
    {
        _weights = weights.Value;
        _options = options.Value;
    }

    public ProviderIntegrityScore Calculate(ProviderVerificationRecord record)
    {
        var score = new ProviderIntegrityScore();
        var dimensions = new List<ScoreDimension>();

        // ── Hard stops ───────────────────────────────────────────
        if (record.ExclusionScreening?.IsExcluded == true)
        {
            score.CompositeScore = 0;
            score.Rating = IntegrityRating.Blocked;
            score.Flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Blocking,
                Source = "LEIE/SAM",
                Code = "EXCLUDED",
                Message = $"Provider is excluded from federal healthcare programs. " +
                          $"Source: {record.ExclusionScreening.Matches.FirstOrDefault()?.Source}"
            });
            return score;
        }

        // ── Dimension 1: NPI Validation ──────────────────────────
        score.NpiValidation = ScoreNpiValidation(record, score.Flags);
        dimensions.Add(score.NpiValidation);

        // ── Dimension 2: Exclusion Screening ─────────────────────
        score.ExclusionScreening = ScoreExclusionScreening(record, score.Flags);
        dimensions.Add(score.ExclusionScreening);

        // ── Dimension 3: Medicare Enrollment ─────────────────────
        score.MedicareEnrollment = ScoreMedicareEnrollment(record, score.Flags);
        dimensions.Add(score.MedicareEnrollment);

        // ── Dimension 4: License Verification ────────────────────
        score.LicenseVerification = ScoreLicenseVerification(record, score.Flags);
        dimensions.Add(score.LicenseVerification);

        // ── Dimension 5: Conflict of Interest ────────────────────
        score.ConflictOfInterest = ScoreConflictOfInterest(record, score.Flags);
        dimensions.Add(score.ConflictOfInterest);

        // ── Composite ────────────────────────────────────────────
        var evaluated = dimensions.Where(d => d.WasEvaluated).ToList();
        if (evaluated.Count > 0)
        {
            var totalWeight = evaluated.Sum(d => d.Weight);
            score.CompositeScore = totalWeight > 0
                ? (int)Math.Round(evaluated.Sum(d => d.Score * d.Weight) / (double)totalWeight)
                : 0;
        }

        score.Rating = score.CompositeScore switch
        {
            >= 80 => IntegrityRating.Clear,
            >= 60 => IntegrityRating.Advisory,
            >= 40 => IntegrityRating.Caution,
            >= 20 => IntegrityRating.Alert,
            _ => IntegrityRating.Blocked
        };

        score.CalculatedAt = DateTimeOffset.UtcNow;
        return score;
    }

    // ── Per-dimension scoring ────────────────────────────────────

    private ScoreDimension ScoreNpiValidation(ProviderVerificationRecord record, List<IntegrityFlag> flags)
    {
        var dim = new ScoreDimension
        {
            Dimension = "NPI Validation",
            Weight = _weights.NpiValidation
        };

        if (record.NppesData is null)
        {
            dim.Score = 0;
            dim.WasEvaluated = true;
            dim.Detail = "NPI not found in NPPES registry";
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Critical,
                Source = "NPPES",
                Code = "NPI_NOT_FOUND",
                Message = "NPI does not exist in the NPPES registry"
            });
            return dim;
        }

        dim.WasEvaluated = true;
        var points = 100;

        if (record.NppesData.NpiStatus == NppesNpiStatus.Deactivated)
        {
            points = 0;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Critical,
                Source = "NPPES",
                Code = "NPI_DEACTIVATED",
                Message = $"NPI was deactivated on {record.NppesData.DeactivationDate:yyyy-MM-dd}"
            });
        }

        // Deduction: no primary taxonomy
        if (record.NppesData.Taxonomies.All(t => !t.IsPrimary))
        {
            points -= 10;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "NPPES",
                Code = "NO_PRIMARY_TAXONOMY",
                Message = "No primary taxonomy designation found"
            });
        }

        // Deduction: no practice location address
        if (!record.NppesData.Addresses.Any(a => a.AddressPurpose == "LOCATION"))
        {
            points -= 10;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "NPPES",
                Code = "NO_PRACTICE_LOCATION",
                Message = "No practice location address on file"
            });
        }

        dim.Score = Math.Max(0, points);
        dim.Detail = $"NPI active since {record.NppesData.EnumerationDate:yyyy-MM-dd}, " +
                     $"{record.NppesData.Taxonomies.Count} taxonomies, " +
                     $"{record.NppesData.Addresses.Count} addresses";

        return dim;
    }

    private ScoreDimension ScoreExclusionScreening(ProviderVerificationRecord record, List<IntegrityFlag> flags)
    {
        var dim = new ScoreDimension
        {
            Dimension = "Exclusion Screening",
            Weight = _weights.ExclusionScreening
        };

        if (record.ExclusionScreening is null)
        {
            dim.WasEvaluated = false;
            dim.Detail = "Exclusion screening not performed";
            return dim;
        }

        dim.WasEvaluated = true;

        if (record.ExclusionScreening.IsExcluded)
        {
            dim.Score = 0; // Should have been caught by hard stop above
        }
        else if (record.ExclusionScreening.Matches.Any(m => m.MatchConfidence >= 0.7f))
        {
            dim.Score = 40;
            dim.Detail = "Possible exclusion match found — manual review recommended";
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "LEIE/SAM",
                Code = "POSSIBLE_EXCLUSION_MATCH",
                Message = "Possible match found on exclusion list — manual review recommended"
            });
        }
        else
        {
            dim.Score = 100;
            dim.Detail = $"Clear — screened at {record.ExclusionScreening.ScreenedAt:yyyy-MM-dd}";
        }

        return dim;
    }

    private ScoreDimension ScoreMedicareEnrollment(ProviderVerificationRecord record, List<IntegrityFlag> flags)
    {
        var dim = new ScoreDimension
        {
            Dimension = "Medicare Enrollment",
            Weight = _weights.MedicareEnrollment
        };

        if (record.PecosStatus is null)
        {
            dim.WasEvaluated = false;
            dim.Detail = "PECOS enrollment data not available";
            return dim;
        }

        dim.WasEvaluated = true;

        if (record.PecosStatus.IsEnrolledInMedicare)
        {
            dim.Score = 100;
            dim.Detail = $"Active Medicare enrollment — {record.PecosStatus.ProviderTypeDescription}";

            // Bonus context: reassignment info
            if (record.PecosStatus.Reassignments.Count > 0)
            {
                dim.Detail += $", {record.PecosStatus.Reassignments.Count} billing org(s)";
            }
        }
        else
        {
            // Not enrolled isn't necessarily bad — Medicaid-only providers won't be here
            dim.Score = 60;
            dim.Detail = "Not found in Medicare FFS enrollment data";
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Info,
                Source = "PECOS",
                Code = "NOT_MEDICARE_ENROLLED",
                Message = "Provider not found in Medicare FFS enrollment — may be Medicaid-only or newly enrolled"
            });
        }

        return dim;
    }

    private ScoreDimension ScoreLicenseVerification(ProviderVerificationRecord record, List<IntegrityFlag> flags)
    {
        var dim = new ScoreDimension
        {
            Dimension = "License Verification",
            Weight = _weights.LicenseVerification
        };

        if (record.FsmbVerification is null)
        {
            dim.WasEvaluated = false;
            dim.Detail = "FSMB license verification not configured (premium tier)";
            return dim;
        }

        dim.WasEvaluated = true;
        var points = 100;

        // Check for active licenses
        var activeLicenses = record.FsmbVerification.Licenses
            .Where(l => l.Status == LicenseStatus.Active)
            .ToList();

        if (activeLicenses.Count == 0)
        {
            points = 10;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Critical,
                Source = "FSMB",
                Code = "NO_ACTIVE_LICENSE",
                Message = "No active state medical license found"
            });
        }

        // Check for disciplinary actions
        var recentActions = record.FsmbVerification.DisciplinaryActions
            .Where(a => a.ActionDate >= DateTimeOffset.UtcNow.AddYears(-5))
            .ToList();

        if (recentActions.Count > 0)
        {
            points -= Math.Min(40, recentActions.Count * 20);
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "FSMB",
                Code = "DISCIPLINARY_ACTION",
                Message = $"{recentActions.Count} disciplinary action(s) in the last 5 years"
            });
        }

        // DEA status
        if (record.FsmbVerification.DeaStatus is DeaRegistrationStatus.Revoked
            or DeaRegistrationStatus.Surrendered)
        {
            points -= 30;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Critical,
                Source = "DEA",
                Code = "DEA_REVOKED",
                Message = $"DEA registration status: {record.FsmbVerification.DeaStatus}"
            });
        }

        // Expiring licenses
        var expiringSoon = activeLicenses
            .Where(l => l.ExpirationDate.HasValue &&
                        l.ExpirationDate.Value <= DateTimeOffset.UtcNow.AddDays(90))
            .ToList();

        if (expiringSoon.Count > 0)
        {
            points -= 10;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Info,
                Source = "FSMB",
                Code = "LICENSE_EXPIRING",
                Message = $"{expiringSoon.Count} license(s) expiring within 90 days"
            });
        }

        dim.Score = Math.Max(0, points);
        dim.Detail = $"{activeLicenses.Count} active license(s), " +
                     $"{recentActions.Count} recent disciplinary action(s), " +
                     $"DEA: {record.FsmbVerification.DeaStatus}";

        return dim;
    }

    private ScoreDimension ScoreConflictOfInterest(ProviderVerificationRecord record, List<IntegrityFlag> flags)
    {
        var dim = new ScoreDimension
        {
            Dimension = "Conflict of Interest",
            Weight = _weights.ConflictOfInterest
        };

        if (record.OpenPaymentsSummary is null)
        {
            dim.WasEvaluated = false;
            dim.Detail = "Open Payments data not available";
            return dim;
        }

        dim.WasEvaluated = true;
        var totalPayments = record.OpenPaymentsSummary.TotalGeneralPayments +
                            record.OpenPaymentsSummary.TotalResearchPayments;

        var highThreshold = _options.OpenPaymentsConflictThreshold * 4; // 100k default
        var moderateThreshold = _options.OpenPaymentsConflictThreshold;  // 25k default

        if (record.OpenPaymentsSummary.HasOwnershipInterest)
        {
            dim.Score = 40;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "OpenPayments",
                Code = "OWNERSHIP_INTEREST",
                Message = "Provider has ownership/investment interest with a reporting entity"
            });
        }
        else if (totalPayments > highThreshold)
        {
            dim.Score = 50;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Warning,
                Source = "OpenPayments",
                Code = "HIGH_PAYMENTS",
                Message = $"Total industry payments: {totalPayments:C0} in PY{record.OpenPaymentsSummary.ProgramYear}"
            });
        }
        else if (totalPayments > moderateThreshold)
        {
            dim.Score = 75;
            flags.Add(new IntegrityFlag
            {
                Severity = IntegrityFlagSeverity.Info,
                Source = "OpenPayments",
                Code = "MODERATE_PAYMENTS",
                Message = $"Total industry payments: {totalPayments:C0} in PY{record.OpenPaymentsSummary.ProgramYear}"
            });
        }
        else
        {
            dim.Score = 100;
            dim.Detail = $"Total industry payments: {totalPayments:C0} — below threshold";
        }

        return dim;
    }
}

/// <summary>
/// Configurable weights for each scoring dimension.
/// Allows health plans to tune the composite score
/// based on their risk tolerance and regulatory requirements.
/// </summary>
public class ScoringWeights
{
    public const string SectionName = "ProviderVerification:ScoringWeights";

    public int NpiValidation { get; set; } = 30;
    public int ExclusionScreening { get; set; } = 30;
    public int MedicareEnrollment { get; set; } = 15;
    public int LicenseVerification { get; set; } = 15;
    public int ConflictOfInterest { get; set; } = 10;
}
