using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Plays the utilization reviewer for the PAS kit (INFERNO-PAS-SERVER-001).
///
/// <para>
/// The kit's pended scenario submits a request CHO cannot decide at once, then
/// waits for CHO to notify it, over the Subscription it created, that the
/// decision has been made. A decision on a pended request is a person's, so
/// while the kit waits the harness makes it the way a reviewer would: through
/// authorization-service's own status API, approving the member's pended
/// request. Delivering the notification is CHO's part, and the kit judges it.
/// </para>
/// </summary>
public static class PasPendedDecision
{
    /// <summary>Answers the kit's wait for the pended-decision notification; ignores every other wait.</summary>
    public static async Task AnswerAsync(InfernoResult waiting, ChoSeedFixture fixture, CancellationToken ct)
    {
        if (waiting.TestId?.EndsWith("subscription_notification_wait", StringComparison.Ordinal) != true)
        {
            return;
        }

        using var http = new HttpClient { Timeout = InteropSettings.Timeouts.ProtocolCall };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DevelopmentToken());
        var root = ChoUnderTest.HostAuthorizationUrl.TrimEnd('/');

        var search = await http.GetAsync(
            $"{root}/api/authorizations/search?memberId={Uri.EscapeDataString(fixture.Member.MemberId)}&status=Pended", ct);
        var body = await search.Content.ReadAsStringAsync(ct);
        if (!search.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"authorization-service answered HTTP {(int)search.StatusCode} to the pended-request search: {Redaction.Body(body)}");
        }

        var pended = (JsonNode.Parse(body) as JsonArray)?.OfType<JsonObject>()
            .OrderByDescending(a => a["submittedDate"]?.GetValue<string>() ?? "", StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InteropEnvironmentException(
                "The PAS kit is waiting for the pended decision, and authorization-service holds no pended request " +
                $"for {fixture.Member.MemberId}, so there is nothing for a reviewer to decide.");

        var id = pended["id"]!.GetValue<string>();
        using var decided = await http.PutAsync($"{root}/api/authorizations/{Uri.EscapeDataString(id)}/status",
            JsonContent.Create(new { status = "Approved", notes = "Approved by the interop harness as the reviewer." }), ct);
        if (!decided.IsSuccessStatusCode)
        {
            throw new InteropEnvironmentException(
                $"authorization-service refused to approve the pended request with HTTP {(int)decided.StatusCode}: " +
                Redaction.Body(await decided.Content.ReadAsStringAsync(ct)));
        }
    }
}
