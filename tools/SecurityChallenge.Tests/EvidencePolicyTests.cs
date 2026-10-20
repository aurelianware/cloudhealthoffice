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
}
