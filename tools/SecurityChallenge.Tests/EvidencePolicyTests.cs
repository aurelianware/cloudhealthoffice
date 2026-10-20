using SecurityChallenge;
using Xunit;

public sealed class EvidencePolicyTests
{
    private static CaseResult[] Passing() => EvidencePolicy.ScenarioIds.Select(id => new CaseResult(id, "PASS", "test")).ToArray();

    [Fact] public void CompleteInventoryPasses() => Assert.True(EvidencePolicy.IsCompletePass(true, Passing()));
    [Theory]
    [InlineData("FAIL")][InlineData("ERROR")][InlineData("INCONCLUSIVE")][InlineData("UNSUPPORTED")][InlineData("pass")]
    public void AnyNonPassBlocksAssurance(string outcome)
    {
        var results = Passing(); results[0] = results[0] with { Outcome = outcome };
        Assert.False(EvidencePolicy.IsCompletePass(true, results));
    }
    [Fact] public void DuplicateCannotReplaceMissingScenario()
    {
        var results = Passing(); results[0] = results[1];
        Assert.False(EvidencePolicy.IsCompletePass(true, results));
    }
    [Fact] public void UnknownScenarioCannotReplaceExpectedScenario()
    {
        var results = Passing(); results[0] = results[0] with { Id = "invented" };
        Assert.False(EvidencePolicy.IsCompletePass(true, results));
    }
    [Fact] public void IncompleteLabNeverPasses() => Assert.False(EvidencePolicy.IsCompletePass(false, Passing()));
    [Fact] public void EmptyRunNeverPasses() => Assert.False(EvidencePolicy.IsCompletePass(true, []));
    [Fact] public void BlockedAndUnsupportedCasesDoNotInflateExecutedCount()
    {
        CaseResult[] results =
        [
            new("control", "PASS", "test"), new("failure", "FAIL", "test"),
            new("execution-error", "ERROR", "test"), new("blocked", "INCONCLUSIVE", "test"),
            new("unavailable", "UNSUPPORTED", "test"), new("lab.prerequisites", "ERROR", "test")
        ];
        Assert.Equal(3, EvidencePolicy.ExecutedCount(results));
    }
    [Fact] public void CampaignCatalogsCannotSatisfyEachOther()
    {
        var second = ClaimsAppealsCampaign.ScenarioIds.Select(id => new CaseResult(id, "PASS", "test")).ToArray();
        Assert.Equal(47, second.Length);
        Assert.Equal(second.Length, second.Select(x => x.Id).Distinct().Count());
        Assert.True(EvidencePolicy.IsCompletePass(true, second, ClaimsAppealsCampaign.ScenarioIds));
        Assert.False(EvidencePolicy.IsCompletePass(true, second));
        Assert.False(EvidencePolicy.IsCompletePass(true, Passing(), ClaimsAppealsCampaign.ScenarioIds));
    }
    [Fact] public void EmptyOrDuplicateExpectedCatalogCannotClaimPass()
    {
        Assert.False(EvidencePolicy.IsCompletePass(true, [], []));
        Assert.False(EvidencePolicy.IsCompletePass(true, [new("x", "PASS", "test"), new("x", "PASS", "test")], ["x", "x"]));
    }
    [Theory]
    [InlineData("FAIL")][InlineData("ERROR")][InlineData("INCONCLUSIVE")][InlineData("UNSUPPORTED")]
    public void FailedControlCannotAuthorizeDependentAssertions(string outcome) =>
        Assert.False(EvidencePolicy.PositiveControlPassed([new("control", outcome, "test")], "control"));
    [Fact] public void OnlyOnePassingControlIsSufficient()
    {
        Assert.True(EvidencePolicy.PositiveControlPassed([new("control", "PASS", "test")], "control"));
        Assert.False(EvidencePolicy.PositiveControlPassed([], "control"));
        Assert.False(EvidencePolicy.PositiveControlPassed([new("control", "PASS", "test"), new("control", "PASS", "test")], "control"));
    }
}
