namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Globalization;
using CloudHealthOffice.ProviderVerificationEngine.Models;

/// <summary>
/// Decides which exclusion-list rows match a provider and how strongly.
/// <para>
/// Policy: only an exact NPI match on a currently effective exclusion is a
/// definitive exclusion (confidence 1.0, <c>IsExcluded</c>). Everything else
/// that plausibly identifies the provider is a possible match (confidence
/// ≥ 0.7) routed to manual review, never an automatic denial. Name-only
/// coincidences that are very likely different people stay below 0.7 and are
/// not reported.
/// </para>
/// </summary>
public static class ExclusionMatcher
{
    public const float Definitive = 1.0f;
    public const float ReviewThreshold = 0.7f;

    public sealed class Outcome
    {
        public bool IsExcluded { get; set; }
        public List<ExclusionMatch> Matches { get; } = [];
    }

    public static Outcome Match(
        ProviderScreeningRequest request,
        IEnumerable<ExclusionRecord> candidates,
        DateTime todayUtc)
    {
        var outcome = new Outcome();
        var requestNpi = ExclusionNameNormalizer.NormalizeNpi(request.Npi);
        var requestLast = ExclusionNameNormalizer.NormalizePersonName(request.LastName, stripSuffixes: true);
        var requestFirst = ExclusionNameNormalizer.NormalizePersonName(request.FirstName);
        var requestOrg = ExclusionNameNormalizer.NormalizeBusinessName(request.OrganizationName);
        var requestDob = request.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in candidates)
        {
            // The same row can appear twice while a dataset swap is in flight.
            var key = $"{record.Source}|{record.Npi}|{record.NormalizedLastName}|{record.NormalizedFirstName}|{record.NormalizedBusinessName}|{record.DobKey}|{record.ExclusionDate:yyyyMMdd}|{record.ExclusionType}";
            if (!seen.Add(key))
                continue;

            if (!IsInEffect(record, todayUtc, out var effectNote, out var pending))
                continue; // reinstated / terminated: no longer excluded

            ExclusionMatch? match = null;

            if (requestNpi is not null && record.Npi == requestNpi)
            {
                match = ToMatch(record, "NPI");
                if (pending || record.WaiverDate is not null || !string.IsNullOrEmpty(record.WaiverState))
                {
                    // A waiver (state-specific permission) or a not-yet-effective
                    // exclusion: confirmed identity, but whether it bars this
                    // claim needs a human. Manual review, not denial.
                    match.MatchConfidence = 0.95f;
                    match.MatchNote = pending
                        ? effectNote
                        : $"NPI match with waiver{(record.WaiverState is null ? string.Empty : $" ({record.WaiverState})")} — verify scope";
                }
                else
                {
                    match.MatchConfidence = Definitive;
                    match.MatchNote = "Exact NPI match on an active exclusion";
                    outcome.IsExcluded = true;
                }
            }
            else if (requestLast.Length > 0 && record.NormalizedLastName == requestLast)
            {
                match = MatchIndividualName(record, requestFirst, requestDob, requestNpi);
            }
            else if (requestOrg.Length > 0 && record.NormalizedBusinessName == requestOrg)
            {
                match = ToMatch(record, "BUSINESS_NAME");
                var npiConflict = requestNpi is not null && record.Npi is not null && record.Npi != requestNpi;
                if (record.NormalizedLastName is not null)
                {
                    // An excluded *individual* listed with this business name
                    // (LEIE BUSNAME on a person row): the organization itself is
                    // not excluded, but an excluded owner/employee is a review item.
                    match.MatchConfidence = ReviewThreshold;
                    match.MatchNote = "Excluded individual associated with this business name — verify ownership/employment";
                }
                else
                {
                    match.MatchConfidence = npiConflict ? 0.75f : 0.85f;
                    match.MatchNote = npiConflict
                        ? "Business name match; listed NPI differs — verify EIN/address"
                        : "Normalized business name match — verify EIN/address";
                }
            }

            if (match is not null && match.MatchConfidence >= ReviewThreshold)
                outcome.Matches.Add(match);
        }

        outcome.Matches.Sort((a, b) => b.MatchConfidence.CompareTo(a.MatchConfidence));
        return outcome;
    }

    private static ExclusionMatch? MatchIndividualName(
        ExclusionRecord record, string requestFirst, string? requestDob, string? requestNpi)
    {
        // First name: exact, initial-compatible, or absent on the list row.
        var recordFirst = record.NormalizedFirstName ?? string.Empty;
        string firstQuality;
        if (requestFirst.Length == 0 || recordFirst.Length == 0)
            firstQuality = "missing";
        else if (recordFirst == requestFirst)
            firstQuality = "exact";
        else if ((recordFirst.Length == 1 || requestFirst.Length == 1) && recordFirst[0] == requestFirst[0])
            firstQuality = "initial";
        else
            return null;

        // DOB: equal, unknown (either side missing) or conflicting.
        var dobKnown = requestDob is not null && record.DobKey is not null;
        if (dobKnown && requestDob != record.DobKey)
            return null; // same name, different birth date: a different person

        var npiConflict = requestNpi is not null && record.Npi is not null && record.Npi != requestNpi;

        float confidence = (firstQuality, dobKnown) switch
        {
            ("exact", true) => 0.95f,
            ("initial", true) => 0.85f,
            ("missing", true) => 0.75f,
            ("exact", false) => 0.7f,
            _ => 0.5f
        };
        if (npiConflict)
        {
            // The list row carries a different NPI: very likely a namesake.
            // Still review when DOB also matches.
            confidence = dobKnown ? Math.Max(ReviewThreshold, confidence - 0.15f) : 0.5f;
        }

        var match = ToMatch(record, dobKnown ? "NAME_DOB" : "NAME");
        match.MatchConfidence = confidence;
        match.MatchNote = (dobKnown ? "Name + date of birth match" : "Name match; date of birth unavailable") +
                          (npiConflict ? "; listed NPI differs" : string.Empty) +
                          " — manual review required";
        return match;
    }

    /// <summary>
    /// False when the row is no longer in effect (reinstated / terminated
    /// / inactive). <paramref name="pending"/> marks an exclusion whose start
    /// date is still in the future.
    /// </summary>
    private static bool IsInEffect(ExclusionRecord record, DateTime todayUtc, out string? note, out bool pending)
    {
        note = null;
        pending = false;
        if (!record.IsActive)
            return false;
        if (record.EndDate is { } end && end.Date <= todayUtc.Date)
            return false;
        if (record.ExclusionDate is { } start && start.Date > todayUtc.Date)
        {
            pending = true;
            note = $"Exclusion effective {start:yyyy-MM-dd} (future) — review";
        }
        return true;
    }

    private static ExclusionMatch ToMatch(ExclusionRecord record, string basis)
    {
        var name = record.BusinessName ?? string.Join(' ',
            new[] { record.FirstName, record.MiddleName, record.LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));

        return new ExclusionMatch
        {
            Source = record.Source,
            ExcludedName = name,
            Npi = record.Npi,
            ExclusionType = record.ExclusionType,
            ExclusionReason = record.ExclusionProgram ?? record.General,
            ExclusionDate = ToOffset(record.ExclusionDate),
            ReinstatementDate = ToOffset(record.EndDate),
            WaiverState = record.WaiverState,
            MatchBasis = basis
        };
    }

    private static DateTimeOffset? ToOffset(DateTime? value) =>
        value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}
