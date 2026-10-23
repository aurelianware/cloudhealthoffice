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
    [Fact]
    public async Task A_group_run_names_the_group_and_a_suite_run_names_the_suite()
    {
        // Inferno decides what to run from which key is present. Sending
        // test_suite_id when a group was meant runs roughly thirty clinical
        // resource groups CHO never claimed, so the two must not blur.
        var recorder = new RequestRecorder();
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(recorder));

        await runner.StartGroupRunAsync("session-1", "us_core_v311_patient", []);
        recorder.LastBody.Should().Contain("\"test_group_id\":\"us_core_v311_patient\"");
        recorder.LastBody.Should().NotContain("test_suite_id");

        await runner.StartSuiteRunAsync("session-1", "us_core_v311", []);
        recorder.LastBody.Should().Contain("\"test_suite_id\":\"us_core_v311\"");
        recorder.LastBody.Should().NotContain("test_group_id");
    }

    [Fact]
    public async Task A_text_input_carries_no_type_and_an_auth_input_carries_one()
    {
        // Inferno's session-data repository switches on `type` and raises
        // UnknownSessionDataType for a non-text input that arrives without one,
        // so an auth_info sent as bare text is refused rather than ignored.
        var recorder = new RequestRecorder();
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(recorder));

        await runner.StartGroupRunAsync("session-1", "us_core_v311_patient",
        [
            new InfernoInput { Name = "url", Value = "http://cho.invalid/fhir/r4" },
            InfernoInput.AccessToken("smart_auth_info", "token-abc"),
        ]);

        recorder.LastBody.Should().Contain("\"name\":\"url\"");
        recorder.LastBody.Should().NotContain("\"name\":\"url\",\"value\":\"http://cho.invalid/fhir/r4\",\"type\"");
        recorder.LastBody.Should().Contain("\"type\":\"auth_info\"");
    }

    [Fact]
    public void An_access_token_input_is_a_JSON_string_rather_than_a_nested_object()
    {
        // Inferno JSON.parses the value, so a nested object would arrive as a
        // Hash the repository refuses.
        var input = InfernoInput.AccessToken("smart_auth_info", "token-abc");

        input.Value.Should().Be("{\"auth_type\":\"public\",\"access_token\":\"token-abc\"}");
        input.Type.Should().Be("auth_info");
    }

    /// <summary>Captures the request body and answers with an empty finished run.</summary>
    private sealed class RequestRecorder : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"run-1","status":"done","results":[]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

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
