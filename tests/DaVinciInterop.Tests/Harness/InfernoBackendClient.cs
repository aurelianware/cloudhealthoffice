using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Registers a conformance kit with cho-smart-auth-service as a SMART Backend
/// Services client, for a scenario whose kit authenticates itself.
///
/// <para>
/// The registration goes through smart-auth-service's own admin API with the
/// seed identity, the same call an operator makes, so the client the kit
/// authenticates as is an ordinary backend client: provider-bound, scoped, and
/// held to the same assertion rules as any other. Nothing about CHO is
/// configured differently for the run.
/// </para>
///
/// <para>
/// The keys are the public half of what the kit publishes at
/// <c>/custom/{suite}/.well-known/jwks.json</c>. Read from the running kit
/// rather than from a copy here, so a pin bump that rotates the kit's keys
/// cannot leave a stale registration behind.
/// </para>
/// </summary>
public static class InfernoBackendClient
{
    /// <summary>JWK members that carry private key material (RFC 7518 §6.2.2, §6.3.2).</summary>
    private static readonly string[] PrivateMembers = ["d", "p", "q", "dp", "dq", "qi", "oth", "k"];

    /// <summary>Where a kit publishes the keys it signs assertions with.</summary>
    public static string JwksUrl(string kitApiBaseUrl, string suiteId)
        => $"{kitApiBaseUrl.TrimEnd('/').RemoveSuffix("/api")}/custom/{suiteId}/.well-known/jwks.json";

    /// <summary>
    /// The verification keys of a JWKS, with any private member removed.
    /// smart-auth-service refuses a registration that carries private key
    /// material, and it has no business in a registration either way.
    /// </summary>
    public static JsonObject PublicKeys(string jwksJson)
    {
        var keys = JsonNode.Parse(jwksJson)?["keys"]?.AsArray()
                   ?? throw new InteropEnvironmentException("The kit's JWKS has no keys array.");

        var publicKeys = new JsonArray();
        foreach (var key in keys.OfType<JsonObject>())
        {
            var copy = (JsonObject)key.DeepClone();
            foreach (var member in PrivateMembers)
            {
                copy.Remove(member);
            }
            publicKeys.Add(copy);
        }

        if (publicKeys.Count == 0)
        {
            throw new InteropEnvironmentException("The kit's JWKS publishes no keys, so it cannot authenticate.");
        }

        return new JsonObject { ["keys"] = publicKeys };
    }

    /// <summary>The admin API body for <paramref name="client"/> with <paramref name="jwks"/>.</summary>
    public static JsonObject RegistrationBody(ChoSeedBackendClient client, JsonObject jwks) => new()
    {
        ["clientId"] = client.ClientId,
        ["kind"] = "backend",
        ["displayName"] = client.DisplayName,
        ["scopes"] = new JsonArray(client.Scopes.Select(scope => (JsonNode)JsonValue.Create(scope)!).ToArray()),
        ["providerId"] = client.ProviderId,
        ["jwks"] = jwks,
    };

    /// <summary>
    /// Reads the kit's keys and registers the client. A client already
    /// registered under the same id (a re-run against a kept stack) is
    /// accepted; any other refusal stops the run before the kit starts.
    /// </summary>
    public static async Task<string> RegisterAsync(
        string kitApiBaseUrl, string suiteId, ChoSeedFixture fixture, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = InteropSettings.Timeouts.ProtocolCall };

        var jwksUrl = JwksUrl(kitApiBaseUrl, suiteId);
        var jwks = PublicKeys(await http.GetStringAsync(jwksUrl, cancellationToken));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ChoUnderTest.HostSmartAuthUrl}/api/admin/smart/clients")
        {
            Content = JsonContent.Create(RegistrationBody(fixture.BackendClient, jwks)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return $"backend client '{fixture.BackendClient.ClientId}' was already registered";
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InteropEnvironmentException(
                $"smart-auth-service refused the backend client registration with HTTP {(int)response.StatusCode}: " +
                Redaction.Body(body));
        }

        return $"backend client '{fixture.BackendClient.ClientId}' registered for provider " +
               $"'{fixture.BackendClient.ProviderId}' with {jwks["keys"]!.AsArray().Count} public keys from {jwksUrl}";
    }

    private static string RemoveSuffix(this string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
}
