namespace SecurityChallenge;

public sealed record CaseResult(string Id, string Outcome, string Assertion);

public static class EvidencePolicy
{
    public static IReadOnlyList<string> ScenarioIds { get; } = Array.AsReadOnly(new[]
    {
        "isolation.a.own-id", "isolation.a.foreign-id", "isolation.a.overlapping-number", "isolation.a.search", "integrity.a.create-overposting",
        "isolation.b.own-id", "isolation.b.foreign-id", "isolation.b.overlapping-number", "isolation.b.search", "integrity.b.create-overposting",
        "identity.write.anonymous", "identity.write.malformed", "identity.write.reader", "privilege.reader-decision",
        "identity.reader-positive-control", "identity.tenant-header-conflict", "identity.wrong-issuer", "identity.wrong-audience",
        "audit.authorized-read-attribution", "privilege.admin-decision-control",
        "isolation.a.foreign-decision", "isolation.b.foreign-decision", "identity.bad-signature",
        "member.approval-control", "member.own-positive-control", "member.foreign-member",
        "member.subscriber-substitution", "member.case-sensitive-id", "member.blank-id",
        "member.wrong-line", "member.wrong-procedure", "member.wrong-provider", "member.trimmed-id-control"
    });

    // Never infer assurance from a successful setup or a count padded with duplicates.
    public static bool IsCompletePass(bool completed, IReadOnlyCollection<CaseResult> results) =>
        completed && results.Count == ScenarioIds.Count
        && results.All(x => x.Outcome == "PASS")
        && results.Select(x => x.Id).Order(StringComparer.Ordinal)
            .SequenceEqual(ScenarioIds.Order(StringComparer.Ordinal));
}
