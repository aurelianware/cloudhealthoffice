using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

internal static class MccClaimDateNormalizer
{
    public static void NormalizeClaimDates(List<SyntheticClaim> claims, int seed)
    {
        var random = new Random(seed);
        var anchorDate = DateTime.UtcNow.Date.AddDays(-45);

        foreach (var claim in claims)
        {
            var originalServiceDate = claim.DateOfService.Date;
            var normalizedServiceDate = anchorDate.AddDays(random.Next(0, 30));
            var dateShift = normalizedServiceDate - originalServiceDate;

            claim.DateOfService = claim.DateOfService.Date.Add(dateShift);
            ShiftMemberCoverageDates(claim.Member, dateShift);
            ShiftNewbornDateOfBirth(claim, dateShift);

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
            claim.DateReceived = latestServiceDate.Date.AddDays(random.Next(1, 15));
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

    private static void ShiftMemberCoverageDates(SyntheticMember member, TimeSpan dateShift)
    {
        if (member.CoverageEffectiveDate != default)
        {
            member.CoverageEffectiveDate = member.CoverageEffectiveDate.Date.Add(dateShift);
        }

        if (member.CoverageTermDate.HasValue)
        {
            member.CoverageTermDate = member.CoverageTermDate.Value.Date.Add(dateShift);
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
    }
}
