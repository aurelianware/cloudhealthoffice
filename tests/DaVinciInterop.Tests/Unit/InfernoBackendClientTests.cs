using System.Text.Json.Nodes;
using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

[Trait("Category", "DaVinciInteropUnit")]
public sealed class InfernoBackendClientTests
{
    [Fact]
    public void The_jwks_url_is_the_kits_own_published_path()
        => InfernoBackendClient.JwksUrl("http://127.0.0.1:14572/api", "bulk_data_v200")
            .Should().Be("http://127.0.0.1:14572/custom/bulk_data_v200/.well-known/jwks.json");

    [Fact]
    public void Without_a_suite_route_the_jwks_url_is_inferno_cores_own()
        => InfernoBackendClient.JwksUrl("http://127.0.0.1:14572/api", null)
            .Should().Be("http://127.0.0.1:14572/jwks.json");

    /// <summary>
    /// The PDex server suite has no JWKS route of its own (the first run got a
    /// 404 there); its auth_info input signs with inferno_core's keys. The Bulk
    /// Data kit keeps its suite route.
    /// </summary>
    [Fact]
    public void The_pdex_kit_registers_inferno_cores_keys_and_bulk_data_its_suites()
    {
        var inventory = InteropScenarioInventory.Load();

        InfernoSuiteScenario.JwksSuiteFor(inventory.Scenario("INFERNO-PDEX-SERVER-001")).Should().BeNull();
        InfernoSuiteScenario.JwksSuiteFor(inventory.Scenario("INFERNO-BULK-SERVER-001")).Should().Be("bulk_data_v200");
    }

    [Fact]
    public void Only_public_key_material_is_registered()
    {
        const string jwks = """
            {"keys":[
              {"kty":"EC","crv":"P-384","x":"X","y":"Y","d":"PRIVATE","kid":"ec","alg":"ES384","key_ops":["verify"]},
              {"kty":"RSA","n":"N","e":"AQAB","d":"PRIVATE","p":"P","q":"Q","dp":"DP","dq":"DQ","qi":"QI","kid":"rsa","alg":"RS384"}
            ]}
            """;

        var keys = InfernoBackendClient.PublicKeys(jwks)["keys"]!.AsArray().OfType<JsonObject>().ToList();

        keys.Select(k => (string?)k["kid"]).Should().BeEquivalentTo(["ec", "rsa"]);
        foreach (var key in keys)
        {
            key.Select(member => member.Key).Should().NotContain(["d", "p", "q", "dp", "dq", "qi"]);
        }
        keys[0]["x"]!.GetValue<string>().Should().Be("X");
        keys[1]["n"]!.GetValue<string>().Should().Be("N");
    }

    [Fact]
    public void An_empty_jwks_stops_the_run()
    {
        var act = () => InfernoBackendClient.PublicKeys("""{"keys":[]}""");
        act.Should().Throw<InteropEnvironmentException>();
    }

    [Fact]
    public void The_payer_registration_is_payer_bound_and_names_no_provider()
    {
        var client = ChoSeedFixture.Load().PayerBackendClient;
        var body = InfernoBackendClient.RegistrationBody(client, new JsonObject { ["keys"] = new JsonArray() });

        body["kind"]!.GetValue<string>().Should().Be("backend");
        body["clientId"]!.GetValue<string>().Should().Be(client.ClientId).And.MatchRegex("^[a-z0-9-]{3,64}$");
        body["payerId"]!.GetValue<string>().Should().Be(client.PayerId).And.MatchRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");
        body.ContainsKey("providerId").Should().BeFalse();
        body["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).Should().BeEquivalentTo(["system/PayerToPayer.read"]);
    }

    [Fact]
    public void The_registration_is_a_provider_bound_backend_client()
    {
        var client = ChoSeedFixture.Load().BackendClient;
        var body = InfernoBackendClient.RegistrationBody(client, new JsonObject { ["keys"] = new JsonArray() });

        body["kind"]!.GetValue<string>().Should().Be("backend");
        body["clientId"]!.GetValue<string>().Should().Be(client.ClientId).And.MatchRegex("^[a-z0-9-]{3,64}$");
        body["providerId"]!.GetValue<string>().Should().Be(client.ProviderId).And.NotBeEmpty();
        body.ContainsKey("payerId").Should().BeFalse();
        body["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).Should().BeEquivalentTo(["system/*.read"]);
    }
}
