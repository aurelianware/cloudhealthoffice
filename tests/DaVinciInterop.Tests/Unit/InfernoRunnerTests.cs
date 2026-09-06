using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The Inferno seam is not exercised end to end yet, so the mapping it will depend
/// on is pinned here — an `omit` counted as a failure, or a suite that skipped
/// everything reported as a pass, would both be misleading conformance evidence.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class InfernoRunnerTests
{
    [Theory]
    [InlineData("pass", InteropStatus.Passed)]
    [InlineData("fail", InteropStatus.Failed)]
    [InlineData("error", InteropStatus.Failed)]
    [InlineData("skip", InteropStatus.Skipped)]
    [InlineData("omit", InteropStatus.Skipped)]
    [InlineData("wait", InteropStatus.Skipped)]
    [InlineData("cancel", InteropStatus.Skipped)]
    [InlineData("running", InteropStatus.NotRun)]
    [InlineData("something-new", InteropStatus.NotRun)]
    public void Inferno_statuses_map_into_the_harness_vocabulary(string infernoResult, InteropStatus expected)
    {
        InfernoRunner.MapStatus(infernoResult).Should().Be(expected);
    }

    [Fact]
    public void Any_failure_fails_the_scenario()
    {
        var run = RunWith("pass", "pass", "fail");

        InfernoRunner.RollUp(run).Should().Be(InteropStatus.Failed);
    }

    [Fact]
    public void A_suite_that_skipped_everything_has_not_proven_interoperability()
    {
        var run = RunWith("skip", "omit");

        InfernoRunner.RollUp(run).Should().Be(InteropStatus.Skipped);
    }

    [Fact]
    public void A_suite_with_no_results_is_NotRun()
    {
        InfernoRunner.RollUp(new InfernoTestRun()).Should().Be(InteropStatus.NotRun);
    }

    [Fact]
    public void A_run_is_only_finished_when_inferno_says_so()
    {
        new InfernoTestRun { Status = "running" }.IsFinished.Should().BeFalse();
        new InfernoTestRun { Status = "done" }.IsFinished.Should().BeTrue();
    }

    private static InfernoTestRun RunWith(params string[] results) => new()
    {
        Id = "run-1",
        Status = "done",
        Results = results.Select((r, i) => new InfernoResult { Id = $"r{i}", Result = r }).ToList(),
    };
}
