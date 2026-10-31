using System.Text.RegularExpressions;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The inventory is what keeps the harness honest about what has and has not been
/// proven. A placeholder must never look like a result.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ScenarioInventoryTests
{
    private static readonly InteropScenarioInventory Inventory = InteropScenarioInventory.Load();
    private static readonly InteropVersions Versions = InteropVersions.Load();

    [Theory]
    [InlineData("BR-CRD-001")]
    [InlineData("BR-DTR-001")]
    [InlineData("BR-PAS-SUBMIT-001")]
    [InlineData("BR-PAS-INQUIRE-001")]
    [InlineData("INFERNO-DTR-PAYER-001")]
    [InlineData("INFERNO-PDEX-SERVER-001")]
    [InlineData("INFERNO-PDEX-CLIENT-001")]
    [InlineData("INFERNO-USCORE-SERVER-001")]
    [InlineData("INFERNO-C4BB-SERVER-001")]
    [InlineData("INFERNO-PLANNET-SERVER-001")]
    [InlineData("INFERNO-BULK-SERVER-001")]
    public void The_planned_scenario_inventory_is_present(string scenarioId)
    {
        Inventory.Scenario(scenarioId).Should().NotBeNull();
    }

    [Fact]
    public void Only_the_scenarios_this_harness_actually_executes_are_marked_implemented()
    {
        // An inventory row is a plan, not a result. This list grows only when a
        // scenario that genuinely crosses into the external implementation lands,
        // so a placeholder can never present itself as proven.
        Inventory.Scenarios
            .Where(s => s.Implemented)
            .Select(s => s.Id)
            .Should().BeEquivalentTo([
                "BR-PAS-SUBMIT-001", "BR-CRD-001", "BR-DTR-001",
                "INFERNO-USCORE-SERVER-001", "INFERNO-C4BB-SERVER-001",
                "INFERNO-PLANNET-SERVER-001", "INFERNO-BULK-SERVER-001",
            ]);
    }

    [Fact]
    public void Every_implemented_scenario_has_a_scenario_test_carrying_its_id()
    {
        // Guards the other direction: a row marked implemented with no test behind
        // it would be reported NotRun forever while claiming to be implemented.
        // TraitAttribute exposes nothing at runtime, so the ids are read from the
        // attribute's constructor arguments in metadata.
        var tested = typeof(InteropVersions).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributesData())
            .Where(data => data.AttributeType == typeof(TraitAttribute)
                           && data.ConstructorArguments.Count == 2
                           && (string?)data.ConstructorArguments[0].Value == "Scenario")
            .Select(data => (string?)data.ConstructorArguments[1].Value)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var scenario in Inventory.Scenarios.Where(s => s.Implemented))
        {
            tested.Should().Contain(scenario.Id,
                "'{0}' is marked implemented, so a scenario test must carry [Trait(\"Scenario\", \"{0}\")]",
                scenario.Id);
        }
    }

    [Fact]
    public void Every_scenario_names_a_pinned_target()
    {
        foreach (var scenario in Inventory.Scenarios)
        {
            var act = () => Versions.Target(scenario.ExternalTarget);
            act.Should().NotThrow($"scenario '{scenario.Id}' names external target '{scenario.ExternalTarget}'");
        }
    }

    [Fact]
    public void Both_directional_roles_are_representable()
    {
        var roles = Inventory.Scenarios.Select(s => s.ParsedChoRole).Distinct().ToList();

        roles.Should().Contain(ChoRole.Client, "CHO drives an external server in the burden-reduction scenarios");
        roles.Should().Contain(ChoRole.Server, "an Inferno suite drives CHO as the system under test");
    }

    [Fact]
    public void Scenarios_that_need_cho_running_say_so_in_their_required_services()
    {
        foreach (var scenario in Inventory.Scenarios.Where(s => s.ParsedChoRole == ChoRole.Server))
        {
            scenario.RequiredServices.Should().Contain("cho-fhir-service",
                "'{0}' has CHO answering the requests, so the environment must start CHO", scenario.Id);
        }
    }

    [Fact]
    public void The_default_developer_path_starts_only_what_the_scenarios_need()
    {
        // The documented commands must not start every external tool. Both
        // br-payer scenarios need exactly one external service.
        Inventory.Scenario("BR-PAS-SUBMIT-001").RequiredServices.Should().BeEquivalentTo(["br-payer"]);
        Inventory.Scenario("BR-CRD-001").RequiredServices.Should().BeEquivalentTo(["br-payer"]);
        Inventory.Scenario("BR-DTR-001").RequiredServices.Should().BeEquivalentTo(["br-payer"]);
    }

    [Fact]
    public void The_dtr_scenario_declares_the_crd_scenario_it_chains_from()
    {
        var dtr = Inventory.Scenario("BR-DTR-001");

        dtr.Protocol.Should().Be("DTR");
        dtr.ParsedChoRole.Should().Be(ChoRole.Client);
        dtr.LinkedFromScenario.Should().Be("BR-CRD-001",
            "the DTR exchange is entered from the payer's own CRD determination, and the inventory should " +
            "say so rather than leaving the chain implicit");

        Inventory.Scenario(dtr.LinkedFromScenario!).Implemented.Should().BeTrue(
            "a scenario cannot chain from one that never runs");
    }

    [Fact]
    public void The_crd_scenario_is_declared_as_cho_driving_an_external_payer()
    {
        var crd = Inventory.Scenario("BR-CRD-001");

        crd.Protocol.Should().Be("CRD");
        crd.ParsedChoRole.Should().Be(ChoRole.Client,
            "CHO is the provider-side CRD client; the external implementation is the payer CDS service");
        crd.ExternalTarget.Should().Be("br-payer");
    }

    /// <summary>
    /// A conformance scenario that names a suite the pinned kit does not ship
    /// fails at container startup, minutes into a CI job, with an Inferno error
    /// rather than a diff. Catching it here costs nothing.
    /// </summary>
    [Fact]
    public void Every_inferno_scenario_names_a_suite_its_pinned_kit_ships()
    {
        foreach (var scenario in Inventory.Scenarios.Where(s => s.InfernoSuite is not null))
        {
            var target = Versions.Target(scenario.ExternalTarget);

            target.Inferno.Should().NotBeNull(
                "'{0}' runs an Inferno suite, so '{1}' must declare an inferno block",
                scenario.Id, target.Key);

            target.Inferno!.Suites.Should().Contain(scenario.InfernoSuite!,
                "'{0}' names suite '{1}'; the pin ships [{2}]",
                scenario.Id, scenario.InfernoSuite, string.Join(", ", target.Inferno.Suites));
        }
    }

    /// <summary>
    /// Every input a conformance scenario declares must be one the harness knows
    /// how to supply. An input silently left unset does not fail the run — the
    /// suite simply tests something other than what the scenario claims, which
    /// is the outcome this inventory exists to make impossible.
    /// </summary>
    /// <summary>
    /// A known kit issue stops a failure counting against CHO, so each one must
    /// say why, name a single test in a group the scenario runs, and appear
    /// once. Anything looser would let a real CHO failure be waved through.
    /// </summary>
    [Fact]
    public void Every_known_kit_issue_names_one_test_the_scenario_runs_and_says_why()
    {
        foreach (var scenario in Inventory.Scenarios)
        {
            foreach (var issue in scenario.InfernoKnownKitIssues)
            {
                issue.Test.Should().NotBeNullOrWhiteSpace($"'{scenario.Id}' lists a known kit issue with no test");
                issue.Test.Should().NotContain(",").And.NotContain("*", "a known kit issue names exactly one test");
                issue.Reason.Length.Should().BeGreaterThan(40, $"'{issue.Test}' needs its reason written down");
                // A scenario that runs the whole suite (no groups named) runs every test.
                if (scenario.InfernoGroups.Count > 0)
                {
                    scenario.InfernoGroups.Should().Contain(
                        group => issue.Test.StartsWith(group + "_", StringComparison.Ordinal),
                        $"'{issue.Test}' must belong to a group '{scenario.Id}' runs");
                }
            }

            scenario.InfernoKnownKitIssues.Select(i => i.Test).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void Carin_bb_runs_the_eob_group_before_the_groups_that_read_its_references()
    {
        // The kit's Coverage, Organization and Practitioner groups take their
        // instances from the references its EOB searches save; run first, they
        // skip with "No Coverage resources appear to be available" (run 41087087059).
        var groups = Inventory.Scenarios.Single(s => s.Id == "INFERNO-C4BB-SERVER-001").InfernoGroups;
        var eob = groups.IndexOf("c4bb_v200_eob");

        eob.Should().BeGreaterThanOrEqualTo(0);
        foreach (var dependent in new[] { "c4bb_v200_coverage", "c4bb_v200_organization", "c4bb_v200_practitioner" })
            groups.IndexOf(dependent).Should().BeGreaterThan(eob, $"{dependent} reads references the EOB group saves");
    }

    [Fact]
    public void The_known_kit_issues_are_the_carin_bb_mixed_claim_types_and_the_plan_net_forward_chains()
    {
        var planNetForwardChains = new[]
        {
            "endpoint_organization_partof",
            "insurance_plan_administered_by_partof", "insurance_plan_owned_by_partof",
            "org_affil_primary_organization_partof", "org_affil_participating_organization_type",
            "org_affil_participating_organization_name", "org_affil_participating_organization_partof",
            "org_affil_network_name", "org_affil_endpoint_organization",
            "practitioner_role_organization_name", "practitioner_role_organization_partof",
            "practitioner_role_organization_type", "practitioner_role_network_name",
            "practitioner_role_network_type", "practitioner_role_endpoint_organization",
            "healthcare_service_organization_name", "healthcare_service_organization_partof",
            "healthcare_service_organization_type", "healthcare_service_endpoint_organization",
            "location_partof_address", "location_partof_organization", "location_organization_name",
            "location_organization_partof", "location_organization_type", "location_endpoint_organization",
        }.Select(chain => ("INFERNO-PLANNET-SERVER-001", $"davinci_plan_net_v110_forward_chain_{chain}_search_test"));

        Inventory.Scenarios.SelectMany(s => s.InfernoKnownKitIssues.Select(i => (s.Id, i.Test)))
            .Should().BeEquivalentTo(planNetForwardChains.Concat(new[]
            {
                ("INFERNO-C4BB-SERVER-001", "c4bb_v200_eob_oral_validation_test"),
                ("INFERNO-C4BB-SERVER-001", "c4bb_v200_eob_pharmacy_validation_test"),
                ("INFERNO-C4BB-SERVER-001", "c4bb_v200_eob_professional_non_clinician_validation_test"),
                ("INFERNO-C4BB-SERVER-001", "c4bb_v200_eob_inpatient_institutional_validation_test"),
                ("INFERNO-C4BB-SERVER-001", "c4bb_v200_eob_outpatient_institutional_validation_test"),
            }), "adding one is a change of claim, so it should be seen here too");
    }

    [Fact]
    public void Every_inferno_scenario_declares_only_inputs_the_harness_can_supply()
    {
        var fixture = ChoSeedFixture.Load();

        foreach (var scenario in Inventory.Scenarios.Where(s => s.InfernoSuite is not null))
        {
            // A patient-app suite is given the token the launch obtains; any value stands in here.
            var patientToken = scenario.InfernoAuth == InfernoAuthModes.PatientApp ? "patient-app-token" : null;
            var act = () => InfernoSuiteScenario.BuildInputs(scenario, fixture, patientToken);

            act.Should().NotThrow($"'{scenario.Id}' declares inputs [{string.Join(", ", scenario.InfernoInputs)}]");

            InfernoSuiteScenario.BuildInputs(scenario, fixture, patientToken)
                .Select(input => input.Name)
                .Should().BeEquivalentTo(scenario.InfernoInputs);
        }
    }

    /// <summary>
    /// A conformance scenario that names no suite would run nothing, and a
    /// server-role scenario that runs nothing is the failure mode the inventory
    /// is supposed to prevent — it would report NotRun while claiming to be
    /// implemented.
    /// </summary>
    [Fact]
    public void Every_implemented_conformance_scenario_declares_what_to_run()
    {
        foreach (var scenario in Inventory.Scenarios
                     .Where(s => s.Implemented && s.ExternalTarget.StartsWith("inferno-", StringComparison.Ordinal)))
        {
            scenario.InfernoSuite.Should().NotBeNullOrWhiteSpace(
                "'{0}' is implemented against a conformance kit, so it must say which suite it runs",
                scenario.Id);

            // A Bulk Data suite names its base bulk_server_url; every other kit, url.
            scenario.InfernoInputs.Should().Contain(
                input => input == "url" || input == "bulk_server_url",
                "'{0}' must tell the suite where CHO is", scenario.Id);
        }
    }

    /// <summary>
    /// The CARIN BB non-financial suite is deliberately not run at the current
    /// pin: it checks the superseded '-NonFinancial' canonicals while CHO
    /// declares the CARIN BB 2.2.0 '-Basis' profiles. If a future pin bump makes
    /// it runnable, that is a decision to take explicitly, not something to
    /// discover from a surprising green row.
    /// </summary>
    [Fact]
    public void The_carin_bb_non_financial_suite_is_not_run_while_its_canonicals_differ()
    {
        Inventory.Scenario("INFERNO-C4BB-SERVER-001").InfernoSuite
            .Should().Be("c4bb_v200",
                "the non-financial suite at this pin cannot match CHO's -Basis profiles in either direction");
    }

    /// <summary>
    /// The Plan-Net suite judges the public directory, which the regulation
    /// makes available without credentials. Pointing it at the authenticated
    /// base, or handing it a token, would test a different claim.
    /// </summary>
    [Fact]
    public void The_plan_net_suite_judges_the_public_directory_without_a_token()
    {
        var scenario = Inventory.Scenario("INFERNO-PLANNET-SERVER-001");

        scenario.ChoSurface.Should().Be(ChoSurfaces.PublicDirectory);
        scenario.InfernoInputs.Should().NotContain("smart_auth_info");
        InfernoSuiteScenario.BuildInputs(scenario, ChoSeedFixture.Load())
            .Single(input => input.Name == "url").Value
            .Should().Be(ChoUnderTest.ContainerPublicDirectoryUrl);
    }

    /// <summary>
    /// The Bulk Data kit authenticates itself, as a provider's bulk client
    /// would. It must ask the token endpoint the kit can reach on the Compose
    /// network (a loopback URL is its own container), as the client the
    /// harness registers, and export the Group that client is bound to.
    /// </summary>
    [Fact]
    public void The_bulk_data_suite_authenticates_with_backend_services_as_the_registered_provider_client()
    {
        var scenario = Inventory.Scenario("INFERNO-BULK-SERVER-001");
        var fixture = ChoSeedFixture.Load();
        var inputs = InfernoSuiteScenario.BuildInputs(scenario, fixture).ToDictionary(i => i.Name);

        scenario.InfernoAuth.Should().Be(InfernoAuthModes.BackendServices);
        inputs["bulk_server_url"].Value.Should().Be(ChoUnderTest.ContainerFhirBaseUrl);
        inputs["group_id"].Value.Should().Be(fixture.BackendClient.ProviderId);

        var auth = System.Text.Json.JsonDocument.Parse(inputs["smart_auth_info"].Value).RootElement;
        inputs["smart_auth_info"].Type.Should().Be("auth_info");
        auth.GetProperty("auth_type").GetString().Should().Be("backend_services");
        auth.GetProperty("token_url").GetString().Should().Be(ChoUnderTest.ContainerTokenUrl)
            .And.NotContain("127.0.0.1").And.NotContain("localhost");
        auth.GetProperty("client_id").GetString().Should().Be(fixture.BackendClient.ClientId);
        auth.GetProperty("requested_scopes").GetString().Should().Be("system/*.read");
    }

    /// <summary>
    /// System-level export is not served (501 by design), and the server TLS
    /// group cannot pass on the plain-HTTP interop network. Running either
    /// would put a known, explained failure in every nightly result. The SMART
    /// Backend Services group runs: its one TLS test is switched to omit by the
    /// kit's own setting (see the compose test below), and the rest of it
    /// judges the token endpoint.
    /// </summary>
    [Fact]
    public void The_bulk_data_suite_runs_smart_backend_services_and_group_and_patient_export()
    {
        Inventory.Scenario("INFERNO-BULK-SERVER-001").InfernoGroups
            .Should().BeEquivalentTo([
                "bulk_data_smart_backend_services_v200",
                "bulk_data_group_export_v200",
                "bulk_data_patient_export_v200",
            ]);
    }

    /// <summary>
    /// Every kit's TLS version tests read INFERNO_DISABLE_TLS_TEST in the worker
    /// that runs them. The interop network is plain HTTP, so without it those
    /// tests fail every night in every kit and say nothing about CHO (US Core
    /// and Plan-Net both failed that way in run 40937514337).
    /// </summary>
    [Fact]
    public void Every_inferno_kit_worker_switches_its_tls_tests_to_omit()
    {
        var compose = File.ReadAllText(InteropPaths.ComposeFile);

        Regex.IsMatch(
                compose,
                @"environment: &inferno-sidecars\n(?:      .*\n)*?      INFERNO_DISABLE_TLS_TEST: ""true""")
            .Should().BeTrue("the shared kit environment must set INFERNO_DISABLE_TLS_TEST");

        var workers = Regex.Matches(compose, @"\n  (?<name>inferno-[\w-]+-worker):\n(?<body>(?:    .*\n|\n)+?)(?=  \S)");
        workers.Should().NotBeEmpty();
        foreach (Match worker in workers)
        {
            worker.Groups["body"].Value.Should().Contain(
                "environment: *inferno-sidecars",
                "'{0}' runs the TLS tests, so it must take the shared kit environment", worker.Groups["name"].Value);
        }
    }

    /// <summary>
    /// benefit-plan-service refuses a plan document on a host it doesn't
    /// allow, which stops the seed and with it every kit (run 40993987533,
    /// after the fixture's host changed and the compose allowlist did not).
    /// </summary>
    [Fact]
    public void Every_seeded_plan_document_is_on_a_host_benefit_plan_service_allows()
    {
        var compose = File.ReadAllText(InteropPaths.ComposeFile);
        var allowed = Regex.Matches(compose, @"BenefitPlan__AllowedDocumentHosts__\d+: (?<host>\S+)")
            .Select(m => m.Groups["host"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(InteropPaths.ChoSeedFixture));
        var documents = fixture.RootElement.GetProperty("directory").GetProperty("plan")
            .GetProperty("documents").EnumerateArray().ToList();

        documents.Should().NotBeEmpty();
        foreach (var document in documents)
        {
            var location = document.GetProperty("location").GetString()!;
            allowed.Should().Contain(new Uri(location).Host,
                "{0} must be on BenefitPlan:AllowedDocumentHosts in the compose file", location);
        }
    }

    /// <summary>
    /// CARIN BB searches ExplanationOfBenefit without naming a patient, which
    /// CHO answers only for a member-bound token (nightly 2026-10-30), so the
    /// suite runs with the token the patient launch obtains, and smart-auth-service
    /// must be running to obtain it.
    /// </summary>
    [Fact]
    public void The_carin_bb_suite_authenticates_as_the_members_patient_app()
    {
        var scenario = Inventory.Scenario("INFERNO-C4BB-SERVER-001");
        var fixture = ChoSeedFixture.Load();

        scenario.InfernoAuth.Should().Be(InfernoAuthModes.PatientApp);
        scenario.RequiredServices.Should().Contain("cho-smart-auth-service");

        var inputs = InfernoSuiteScenario.BuildInputs(scenario, fixture, "patient-token").ToDictionary(i => i.Name);
        var auth = System.Text.Json.JsonDocument.Parse(inputs["smart_auth_info"].Value).RootElement;
        auth.GetProperty("access_token").GetString().Should().Be("patient-token");
        inputs["patient_ids"].Value.Should().Be(fixture.Member.MemberId);

        var act = () => InfernoSuiteScenario.BuildInputs(scenario, fixture);
        act.Should().Throw<InteropEnvironmentException>("a run must never fall back to a token bound to nobody");
    }

    [Fact]
    public void The_patient_app_asks_only_for_what_a_patient_app_may_hold()
    {
        var app = ChoSeedFixture.Load().PatientApp;

        app.ClientId.Should().NotBeNullOrWhiteSpace();
        app.Username.Should().NotBeNullOrWhiteSpace();
        new Uri(app.RedirectUri).Scheme.Should().Be("https", "smart-auth-service accepts only HTTPS redirects outside loopback");
        app.Scopes.Should().Contain("launch/patient")
            .And.OnlyContain(scope => scope == "launch/patient" || scope.StartsWith("patient/"));
        InfernoPatientApp.RegistrationBody(app)["kind"]!.GetValue<string>().Should().Be("patient-app");
    }

    [Fact]
    public void Every_scenario_names_a_known_auth_mode()
    {
        foreach (var scenario in Inventory.Scenarios)
        {
            scenario.InfernoAuth.Should().BeOneOf(
                InfernoAuthModes.ChoToken, InfernoAuthModes.BackendServices, InfernoAuthModes.PatientApp);
        }
    }

    [Fact]
    public void Every_scenario_names_a_surface_the_harness_knows()
    {
        foreach (var scenario in Inventory.Scenarios)
        {
            scenario.ChoSurface.Should().BeOneOf(ChoSurfaces.Fhir, ChoSurfaces.PublicDirectory);
        }
    }
}
