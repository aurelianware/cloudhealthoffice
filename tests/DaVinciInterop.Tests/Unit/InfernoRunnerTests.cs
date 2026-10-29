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
    [Fact]
    public void Required_inputs_the_scenario_omits_take_the_kits_own_defaults()
    {
        // Inferno's API refuses a run that leaves a required input unset, even
        // one the kit gives a default (Plan-Net's no_param_search, max_instances
        // and max_pages); its UI would have pre-filled them.
        var declared = System.Text.Json.JsonSerializer.Deserialize<List<InfernoDeclaredInput>>(
            """
            [
              {"name":"url"},
              {"name":"smart_auth_info","optional":true},
              {"name":"no_param_search","default":"true"},
              {"name":"max_instances","default":"200"},
              {"name":"optional_ids","optional":true,"default":"x"},
              {"name":"no_default"}
            ]
            """)!;
        var supplied = new List<InfernoInput> { new() { Name = "url", Value = "http://cho/fhir" } };

        var (inputs, defaulted) = InfernoRunner.WithDeclaredDefaults(supplied, declared);

        inputs.Select(i => (i.Name, i.Value)).Should().Equal(
            ("url", "http://cho/fhir"), ("no_param_search", "true"), ("max_instances", "200"));
        defaulted.Should().Equal("no_param_search", "max_instances");
    }

    [Fact]
    public void A_supplied_input_is_never_replaced_by_a_default()
    {
        var declared = new List<InfernoDeclaredInput>
        {
            new() { Name = "max_pages", Default = System.Text.Json.JsonDocument.Parse("\"20\"").RootElement },
        };
        var supplied = new List<InfernoInput> { new() { Name = "max_pages", Value = "5" } };

        var (inputs, defaulted) = InfernoRunner.WithDeclaredDefaults(supplied, declared);

        inputs.Should().ContainSingle().Which.Value.Should().Be("5");
        defaulted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_session_reports_what_the_kit_said()
    {
        // A bare "500 (Internal Server Error)" is all the nightly run showed;
        // the kit's own message only reached the artifacts.
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(new Refusing()));

        var act = () => runner.CreateSessionAsync("davinci_plan_net_server_v110");

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("HTTP 500").And.Contain("validator unreachable");
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"errors":"validator unreachable"}"""),
            });
    }

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

    /// <summary>
    /// Inferno addresses a nested group by an id the kit's source never writes
    /// down: <c>us_core_v311_patient</c> is declared in the kit and runnable
    /// only as <c>us_core_v311-us_core_v311_fhir_api-us_core_v311_patient</c>.
    /// Composing that prefix from a naming rule would be a guess, and a wrong
    /// guess names a group that does not exist rather than failing loudly.
    /// </summary>
    [Fact]
    public void A_declared_group_id_resolves_to_the_nested_runnable_id()
    {
        var suite = SampleSuite();

        InfernoRunner.ResolveGroupId(suite, "us_core_v311_patient")
            .Should().Be("us_core_v311-us_core_v311_fhir_api-us_core_v311_patient");

        InfernoRunner.ResolveGroupId(suite, "us_core_v311_capability_statement")
            .Should().Be("us_core_v311-us_core_v311_fhir_api-us_core_v311_capability_statement");
    }

    [Fact]
    public void A_full_runnable_id_resolves_to_itself()
    {
        InfernoRunner.ResolveGroupId(SampleSuite(), "us_core_v311-us_core_smart_app_launch")
            .Should().Be("us_core_v311-us_core_smart_app_launch");
    }

    [Fact]
    public void A_group_the_suite_does_not_have_is_an_error_naming_the_ones_it_does()
    {
        var act = () => InfernoRunner.ResolveGroupId(SampleSuite(), "us_core_v311_allergy_intolerance");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*us_core_v311_allergy_intolerance*")
            .WithMessage("*us_core_v311-us_core_v311_fhir_api-us_core_v311_patient*",
                "a scenario naming a group that does not exist must be told which ones do");
    }

    /// <summary>
    /// Suffix matching is what makes resolution possible at all, and it is also
    /// how it could go wrong: two groups ending in the same declared id would
    /// make the first match arbitrary. An ambiguous id is an error, never a
    /// silent choice between two suites' worth of tests.
    /// </summary>
    [Fact]
    public void An_ambiguous_declared_id_is_an_error_rather_than_a_first_match()
    {
        var suite = new InfernoTestSuite
        {
            Id = "c4bb_v200",
            TestGroups =
            [
                new InfernoTestGroup { Id = "c4bb_v200-a-capability_statement_group" },
                new InfernoTestGroup { Id = "c4bb_v200-b-capability_statement_group" },
            ],
        };

        var act = () => InfernoRunner.ResolveGroupId(suite, "capability_statement_group");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ambiguous*")
            .WithMessage("*c4bb_v200-a-capability_statement_group*")
            .WithMessage("*c4bb_v200-b-capability_statement_group*");
    }

    [Fact]
    public void A_partial_word_match_is_not_a_match()
    {
        // "patient" is a suffix of "us_core_v311_patient" as a string, but not as
        // an id segment. Matching on the bare substring would resolve one group's
        // id to another's.
        var act = () => InfernoRunner.ResolveGroupId(SampleSuite(), "patient");

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>The shape Inferno's /test_suites/{id} returns, trimmed to what the resolver reads.</summary>
    private static InfernoTestSuite SampleSuite() => new()
    {
        Id = "us_core_v311",
        Title = "US Core Server v3.1.1",
        TestGroups =
        [
            new InfernoTestGroup { Id = "us_core_v311-us_core_smart_app_launch" },
            new InfernoTestGroup
            {
                Id = "us_core_v311-us_core_v311_fhir_api",
                TestGroups =
                [
                    new InfernoTestGroup { Id = "us_core_v311-us_core_v311_fhir_api-us_core_v311_capability_statement" },
                    new InfernoTestGroup { Id = "us_core_v311-us_core_v311_fhir_api-us_core_v311_patient" },
                    new InfernoTestGroup { Id = "us_core_v311-us_core_v311_fhir_api-us_core_v311_encounter" },
                ],
            },
        ],
    };
}
