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

    [Fact]
    public async Task Requests_to_the_kit_carry_a_content_length_and_are_never_chunked()
    {
        // Puma buffers a chunked body to a Tempfile, and Inferno's API calls
        // request.body.string on it: every session and run answered 500 in CI.
        var recorder = new RequestRecorder();
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(recorder));

        await runner.StartSuiteRunAsync("session-1", "us_core_v311", []);

        recorder.LastContentLength.Should().Be(System.Text.Encoding.UTF8.GetByteCount(recorder.LastBody));
    }

    private sealed class RequestRecorder : HttpMessageHandler
    {
        public string LastBody { get; private set; } = "";
        public long? LastContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastContentLength = request.Content?.Headers.ContentLength;
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

    [Fact]
    public void A_token_the_kit_obtained_reaches_the_next_group_instead_of_being_overwritten()
    {
        // Run 40956429968: the export groups were sent the harness's original
        // smart_auth_info (no token), which replaced the token the SMART group
        // had stored, and every kick-off answered 401.
        var original = InfernoInput.BackendServices(
            "smart_auth_info", "http://cho-smart-auth-service:8080/connect/token", "inferno-bulk-data",
            "system/*.read");
        var supplied = new List<InfernoInput>
        {
            original,
            new() { Name = "bulk_server_url", Value = "http://cho-fhir-service:8080/fhir/r4" },
        };
        var session = new Dictionary<string, string>
        {
            ["smart_auth_info"] = """{"auth_type":"backend_services","access_token":"issued"}""",
            ["bulk_server_url"] = "http://somewhere-else.invalid",
        };

        var (inputs, carried) = InfernoRunner.WithSessionAuth(supplied, session);

        carried.Should().Equal("smart_auth_info");
        inputs.Single(i => i.Name == "smart_auth_info").Should().Be(
            original with { Value = """{"auth_type":"backend_services","access_token":"issued"}""" });
        inputs.Single(i => i.Name == "bulk_server_url").Value.Should().Be(
            "http://cho-fhir-service:8080/fhir/r4", "only auth_info is the kit's to fill in");
    }

    [Fact]
    public void An_auth_input_the_session_has_not_changed_is_not_reported_as_carried()
    {
        var original = InfernoInput.AccessToken("smart_auth_info", "minted");
        var (inputs, carried) = InfernoRunner.WithSessionAuth(
            [original], new Dictionary<string, string> { ["smart_auth_info"] = original.Value });

        carried.Should().BeEmpty();
        inputs.Should().Equal(original);
    }

    [Fact]
    public async Task Session_data_is_read_by_name_and_empty_values_are_left_out()
    {
        var handler = new Answering(
            """[{"name":"smart_auth_info","value":"{\"access_token\":\"t\"}"},{"name":"group_id","value":null},{"name":"since_timestamp","value":""}]""");
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(handler));

        var data = await runner.ReadSessionDataAsync("session-1");

        handler.LastUri.Should().Be("http://inferno.invalid/api/test_sessions/session-1/session_data");
        data.Should().Equal(new Dictionary<string, string> { ["smart_auth_info"] = """{"access_token":"t"}""" });
    }

    [Fact]
    public async Task A_refused_requests_response_body_is_read_back()
    {
        var handler = new Answering(
            """{"id":"req-1","status":400,"response_body":"{\"resourceType\":\"OperationOutcome\"}"}""");
        var runner = new InfernoRunner("http://inferno.invalid/api", new HttpClient(handler));

        var body = await runner.ReadResponseBodyAsync("req-1");

        handler.LastUri.Should().Be("http://inferno.invalid/api/requests/req-1");
        body.Should().Be("""{"resourceType":"OperationOutcome"}""");
    }

    [Fact]
    public void A_results_messages_and_requests_are_read_from_the_run()
    {
        var result = System.Text.Json.JsonSerializer.Deserialize<InfernoResult>(
            """
            {"id":"r1","test_id":"t1","result":"fail","result_message":"1 / 1 Patient resources failed",
             "messages":[{"type":"error","message":"Patient.name: minimum required = 1"}],
             "requests":[{"id":"q1","verb":"get","url":"http://cho/fhir/r4/Patient?_id=x","status":400}]}
            """)!;

        result.Messages.Should().ContainSingle().Which.Message.Should().Contain("Patient.name");
        result.Requests.Should().ContainSingle().Which.Status.Should().Be(400);
    }

    private sealed class Answering(string body) : HttpMessageHandler
    {
        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
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

    private static readonly IReadOnlyList<InfernoKnownKitIssue> OralIssue =
    [
        new() { Test = "c4bb_v200_eob_oral_validation_test", Reason = "validates every EOB against the oral profile" },
    ];

    private static InfernoTestRun GroupRun(params (string TestId, string Result)[] tests) => new()
    {
        Status = "done",
        Results =
        [
            .. tests.Select(t => new InfernoResult { TestId = $"c4bb_v200-Group02-c4bb_v200_eob-{t.TestId}", Result = t.Result }),
            new InfernoResult { TestGroupId = "c4bb_v200-Group02-c4bb_v200_eob", Result = tests.Any(t => t.Result == "fail") ? "fail" : "pass" },
        ],
    };

    [Fact]
    public void A_known_kit_issue_does_not_fail_the_run_nor_does_the_group_row_it_fails()
    {
        var run = GroupRun(("c4bb_v200_eob_read_test", "pass"), ("c4bb_v200_eob_oral_validation_test", "fail"));

        InfernoRunner.RollUp(run).Should().Be(InteropStatus.Failed, "without the issue named, the failure counts");
        InfernoRunner.RollUp(run, OralIssue).Should().Be(InteropStatus.Passed);
    }

    [Fact]
    public void A_known_kit_issue_excuses_only_its_own_test()
    {
        var run = GroupRun(
            ("c4bb_v200_eob_oral_validation_test", "fail"),
            ("c4bb_v200_eob__lastUpdated_search_test", "fail"));

        InfernoRunner.RollUp(run, OralIssue).Should().Be(InteropStatus.Failed);
    }

    [Fact]
    public void Only_tests_count_as_passes_once_an_issue_is_excused()
    {
        // The group row passing proves nothing beyond its tests; with every test
        // excused or skipped, the run has proven nothing.
        var run = GroupRun(("c4bb_v200_eob_oral_validation_test", "fail"), ("c4bb_v200_eob_read_test", "skip"));

        InfernoRunner.RollUp(run, OralIssue).Should().Be(InteropStatus.Skipped);
    }

    [Fact]
    public void A_parent_group_row_failed_by_an_earlier_group_run_does_not_fail_a_later_one()
    {
        // Groups run one at a time in one session (as run 41133414552 ran
        // c4bb_v200_eob, then c4bb_v200_coverage): Inferno's row for their
        // parent Group02 in the coverage run rolls up the EOB group's excused
        // failure, while the coverage run's own tests passed or skipped.
        var coverage = new InfernoTestRun
        {
            Status = "done",
            Results =
            [
                new InfernoResult { TestId = "c4bb_v200-Group02-c4bb_v200_coverage-c4bb_v200_coverage_read_test", Result = "pass" },
                new InfernoResult { TestId = "c4bb_v200-Group02-c4bb_v200_coverage-c4bb_v200_coverage__id_search_test", Result = "skip" },
                new InfernoResult { TestGroupId = "c4bb_v200-Group02-c4bb_v200_coverage", Result = "pass" },
                new InfernoResult { TestGroupId = "c4bb_v200-Group02", Result = "fail" },
            ],
        };

        InfernoRunner.RollUp(coverage, OralIssue).Should().Be(InteropStatus.Passed);
        InfernoRunner.RollUp(coverage).Should().Be(InteropStatus.Passed,
            "the parent's failure belongs to the run of the group whose test failed, and is counted there");
    }

    [Theory]
    [InlineData("c4bb_v200_eob_oral_validation_test", true)]
    [InlineData("c4bb_v200-Group02-c4bb_v200_eob-c4bb_v200_eob_oral_validation_test", true)]
    [InlineData("c4bb_v200-Group02-c4bb_v200_eob-xc4bb_v200_eob_oral_validation_test", false)]
    [InlineData("c4bb_v200_eob_oral_validation_test_2", false)]
    [InlineData(null, false)]
    public void A_known_kit_issue_names_one_test_by_its_declared_id(string? testId, bool matches)
        => OralIssue[0].Matches(testId).Should().Be(matches);

    [Fact]
    public void Test_counts_are_per_test_in_infernos_vocabulary()
    {
        var run = GroupRun(
            ("c4bb_v200_eob_read_test", "pass"),
            ("c4bb_v200_eob__lastUpdated_search_test", "fail"),
            ("c4bb_v200_eob_type_search_test", "error"),
            ("c4bb_v200_eob_oral_validation_test", "fail"),
            ("c4bb_v200_eob_identifier_search_test", "skip"),
            ("c4bb_v200_eob_wait_test", "wait"),
            ("c4bb_v200_eob_tls_test", "omit"));

        InfernoTestCounts.From([run], OralIssue).Should().BeEquivalentTo(new InfernoTestCounts
        {
            Passed = 1, Failed = 2, KnownKitIssue = 1, Skipped = 2, Omitted = 1, Total = 7,
        }, "the failing group row is not a test, and the oral failure is a known kit issue");
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
