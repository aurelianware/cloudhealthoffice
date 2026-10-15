using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccClaimDateNormalizer
{
    /// <summary>Longest gap drawn between the last service date and <see cref="SyntheticClaim.DateReceived"/>.</summary>
    internal const int MaxReceiptLagDays = 14;

    /// <summary>
    /// Re-dates every claim into the weeks just before
    /// <paramref name="referenceDate"/> (default: today, UTC) while preserving
    /// each claim's internal date relationships (length of stay, newborn age,
    /// coverage windows, accident date). No service, discharge or receipt date
    /// lands after the reference date: a claim whose stay is long enough to run
    /// past it is moved earlier as a whole. Deterministic for a given seed and
    /// reference date.
    /// </summary>
    public static void NormalizeClaimDates(List<SyntheticClaim> claims, int seed, DateTime? referenceDate = null)
    {
        var random = new Random(seed);
        var today = (referenceDate ?? DateTime.UtcNow).Date;
        var anchorDate = today.AddDays(-45);

        foreach (var claim in claims)
        {
            var originalServiceDate = claim.DateOfService.Date;

            // Days from the date of service to the last line's service-to date
            // (an institutional stay runs up to ~3 months). The latest service
            // date plus the longest receipt lag must not pass the reference
            // date, so a long stay starts earlier than the 30-day window below
            // would otherwise put it.
            var serviceSpanDays = Math.Max(0, claim.Lines
                .Select(line => (line.ServiceEndDate ?? line.ServiceDate).Date)
                .DefaultIfEmpty(originalServiceDate)
                .Max()
                .Subtract(originalServiceDate).Days);
            var latestAllowedServiceDate = today.AddDays(-(serviceSpanDays + MaxReceiptLagDays));

            var normalizedServiceDate = anchorDate.AddDays(random.Next(0, 30));
            if (normalizedServiceDate > latestAllowedServiceDate)
            {
                normalizedServiceDate = latestAllowedServiceDate;
            }

            var dateShift = normalizedServiceDate - originalServiceDate;

            claim.DateOfService = claim.DateOfService.Date.Add(dateShift);
            ShiftMemberCoverageDates(claim.Member, dateShift, claim.DateOfService);
            ShiftNewbornDateOfBirth(claim, dateShift);

            if (claim.AccidentDate.HasValue)
            {
                claim.AccidentDate = claim.AccidentDate.Value.Date.Add(dateShift);
            }

            foreach (var line in claim.Lines)
            {
                line.ServiceDate = line.ServiceDate.Date.Add(dateShift);
                if (line.ServiceEndDate.HasValue)
                {
                    line.ServiceEndDate = line.ServiceEndDate.Value.Date.Add(dateShift);
                }
            }

            var latestServiceDate = claim.Lines
                .Select(line => line.ServiceEndDate ?? line.ServiceDate)
                .DefaultIfEmpty(claim.DateOfService)
                .Max();
            // Answer-key receipt date only: claims-service stamps the payer
            // receipt date itself at intake and ignores any submitted value.
            claim.DateReceived = latestServiceDate.Date.AddDays(random.Next(1, MaxReceiptLagDays + 1));
        }
    }

    private static void ShiftNewbornDateOfBirth(SyntheticClaim claim, TimeSpan dateShift)
    {
        if (claim.EdgeCase is not (
            EdgeCaseScenario.NewbornAutoAdjudication or
            EdgeCaseScenario.NewbornMotherClaimLink or
            EdgeCaseScenario.NewbornFirstThirtyDays))
        {
            return;
        }

        claim.Member.DateOfBirth = claim.Member.DateOfBirth.Date.Add(dateShift);
    }

    private static void ShiftMemberCoverageDates(SyntheticMember member, TimeSpan dateShift, DateTime serviceDate)
    {
        if (member.CoverageEffectiveDate != default)
        {
            member.CoverageEffectiveDate = member.CoverageEffectiveDate.Date.Add(dateShift);
        }

        if (member.CoverageTermDate.HasValue)
        {
            member.CoverageTermDate = member.CoverageTermDate.Value.Date.Add(dateShift);
        }

        if (member.PlanChangeEffectiveDate.HasValue)
        {
            member.PlanChangeEffectiveDate = member.PlanChangeEffectiveDate.Value.Date.Add(dateShift);
        }

        foreach (var coverage in member.Coverages)
        {
            if (coverage.EffectiveDate != default)
            {
                coverage.EffectiveDate = coverage.EffectiveDate.Date.Add(dateShift);
            }

            if (coverage.TermDate.HasValue)
            {
                coverage.TermDate = coverage.TermDate.Value.Date.Add(dateShift);
            }
        }

        // The base member generator (InMemoryReferenceDataProvider.GenerateMember)
        // draws CoverageEffectiveDate from a fixed 2023 window, uncorrelated with
        // any claim's service date. Shifting it by the claim's own date delta
        // preserves whatever relative ordering happened to exist -- good or bad --
        // rather than fixing it, so roughly 1% of claims end up with a member
        // whose coverage becomes effective after their own service date, denying
        // CARC_27 against scenarios that never intended to test that boundary.
        // Scenarios that need a specific effective/service relationship
        // (RetroEligibilityTermination, the newborn scenarios) already establish
        // one explicitly, earlier in generation, before this normalizer runs --
        // this only corrects claims that would otherwise be left with an
        // unintentionally invalid eligibility window.
        if (member.CoverageEffectiveDate.Date > serviceDate.Date)
        {
            var correctedEffectiveDate = serviceDate.Date.AddYears(-1);
            var correctionShift = correctedEffectiveDate - member.CoverageEffectiveDate.Date;
            member.CoverageEffectiveDate = correctedEffectiveDate;

            foreach (var coverage in member.Coverages)
            {
                if (coverage.EffectiveDate != default)
                {
                    coverage.EffectiveDate = coverage.EffectiveDate.Date.Add(correctionShift);
                }
            }
        }
    }
}
