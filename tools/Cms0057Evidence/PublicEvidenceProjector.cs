namespace Cms0057Evidence;

public sealed class PublicEvidenceException : Exception
{
    public PublicEvidenceException(string message) : base(message) { }
}

/// <summary>
/// Projects a full <see cref="EvidenceReport"/> into a sanitized
/// <see cref="PublicEvidence"/> for publication on the website.
///
/// Invariants:
///  * Allow-list only — a fresh <see cref="PublicEvidence"/> is built field by
///    field; the raw report is never serialized and field-stripped afterward.
///  * DECLARED capability status is used everywhere (a passing GAP-assertion test
///    stays GAP).
///  * Cloud Health Office Replace (product) and each Augment backend (integration)
///    are counted independently.
///  * A run with acceptance-test failures cannot be published.
/// </summary>
public static class PublicEvidenceProjector
{
    private const string GithubServerUrl = "https://github.com";
    private const string AugmentPrefix = "augment.";

    public static PublicEvidence Project(EvidenceReport report)
    {
        if (report is null)
            throw new PublicEvidenceException("Evidence report is null; nothing to publish.");
        if (report.TestSummary.Failed > 0)
            throw new PublicEvidenceException(
                $"Refusing to publish public evidence: {report.TestSummary.Failed} acceptance test(s) failed. "
                + "A public snapshot is only produced from a fully passing, validated run.");

        var scenarios = report.Scenarios
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(ToPublicScenario)
            .ToList();

        var sha = report.Identity.CommitSha;
        var shortSha = string.IsNullOrEmpty(sha) ? null : sha[..Math.Min(12, sha.Length)];
        var repo = report.Identity.Repository;
        var sourceCommitUrl = (!string.IsNullOrEmpty(sha) && !string.IsNullOrEmpty(repo))
            ? $"{GithubServerUrl}/{repo}/commit/{sha}"
            : null;

        return new PublicEvidence
        {
            SchemaVersion = report.SchemaVersion,
            EvidenceStatus = "validated",
            GeneratedAtUtc = report.Identity.GeneratedAtUtc,
            CommitSha = sha,
            CommitShaShort = shortSha,
            SourceCommitUrl = sourceCommitUrl,
            TestDataClassification = report.Identity.TestDataClassification,
            Framework = report.Identity.Framework,
            FhirVersion = report.Identity.FhirVersion,
            ScenarioCount = scenarios.Count,
            TestSummary = new PublicTestSummary
            {
                Passed = report.TestSummary.Passed,
                Failed = report.TestSummary.Failed,
                Skipped = report.TestSummary.Skipped,
            },
            ReplaceSummary = ToCounts(scenarios.Select(s => s.Replace)),
            Integrations = CountIntegrations(scenarios),
            Scenarios = scenarios,
            Disclaimers = new List<string>
            {
                "PASSABLE means the tested Cloud Health Office acceptance scenario is supported by the "
                + "referenced implementation and source revision. It is not CMS certification and does not "
                + "establish production readiness for a specific payer deployment.",
                "External-core integration status is reported separately from Cloud Health Office product capability.",
                "Evidence is generated from the acceptance suite on synthetic data at the referenced source revision.",
            },
        };
    }

    private static PublicScenario ToPublicScenario(ScenarioEvidence s)
    {
        var replace = s.Backends.FirstOrDefault(b => b.Backend == BackendIds.Replace)?.DeclaredStatus
                      ?? Status.NotApplicable;

        var integrations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var b in s.Backends.Where(b => b.Backend.StartsWith(AugmentPrefix, StringComparison.Ordinal)))
            integrations[b.Backend[AugmentPrefix.Length..]] = b.DeclaredStatus;

        return new PublicScenario
        {
            Id = s.Id,
            Name = s.Name,
            Capability = s.Capability,
            Replace = replace,
            Integrations = integrations,
        };
    }

    /// <summary>Counts declared statuses per external-core backend, independently
    /// of the Replace (product) tally. Backend keys are discovered from the data,
    /// so an unknown future backend flows through without code changes.</summary>
    private static SortedDictionary<string, StatusCounts> CountIntegrations(IEnumerable<PublicScenario> scenarios)
    {
        var byKey = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in scenarios)
            foreach (var (key, status) in s.Integrations)
            {
                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = new List<string>();
                list.Add(status);
            }

        var result = new SortedDictionary<string, StatusCounts>(StringComparer.Ordinal);
        foreach (var (key, list) in byKey)
            result[key] = ToCounts(list);
        return result;
    }

    private static StatusCounts ToCounts(IEnumerable<string> statuses)
    {
        int passable = 0, partial = 0, gap = 0, na = 0;
        foreach (var status in statuses)
        {
            switch (status)
            {
                case Status.Passable: passable++; break;
                case Status.Partial: partial++; break;
                case Status.Gap: gap++; break;
                case Status.NotApplicable: na++; break;
            }
        }
        return new StatusCounts { Passable = passable, Partial = partial, Gap = gap, Na = na };
    }
}
