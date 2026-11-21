using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    /// <summary>How long the harness lets CHO's handshake reach the kit before confirming it.</summary>
    public static readonly TimeSpan HandshakeAllowance = TimeSpan.FromSeconds(10);

    private static readonly Regex ResumeLink = new(@"\]\((?<url>https?://[^)\s]+/resume_pass\?[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// The Subscription handshake wait. Matched anywhere in the id: the kit
    /// prefixes its suite and group, and may suffix a variant.
    /// </summary>
    public static bool IsHandshakeWait(string? testId)
        => testId?.Contains("notification_delivery", StringComparison.Ordinal) == true;

    /// <summary>
    /// The pended-decision notification wait, e.g.
    /// <c>pas_server_v221_subscription_notification_wait_pended</c>.
    /// </summary>
    public static bool IsDecisionWait(string? testId)
        => testId?.Contains("subscription_notification_wait", StringComparison.Ordinal) == true;

    /// <summary>
    /// Answers the kit's two waits: the Subscription handshake (confirmed once
    /// CHO has had time to send it) and the pended-decision notification (by
    /// deciding the request). Ignores every other wait.
    /// </summary>
    public static async Task AnswerAsync(
        InfernoResult waiting, ChoSeedFixture fixture, string kitApiBaseUrl, CancellationToken ct)
    {
        if (IsHandshakeWait(waiting.TestId))
        {
            await ConfirmHandshakeAsync(waiting, kitApiBaseUrl, ct);
            return;
        }

        if (!IsDecisionWait(waiting.TestId))
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

    /// <summary>
    /// The handshake test waits for the person running the kit to say the
    /// server has sent its notifications, by opening the kit's "Click here"
    /// link. CHO sends the handshake a moment after it accepts the
    /// Subscription, so the harness waits for that, then opens the link the
    /// kit gave, on the port the harness reaches the kit on. Whether the
    /// handshake arrived, and was right, is the kit's to judge.
    /// </summary>
    private static async Task ConfirmHandshakeAsync(InfernoResult waiting, string kitApiBaseUrl, CancellationToken ct)
    {
        var text = string.Join('\n', new[] { waiting.ResultMessage ?? "" }.Concat(waiting.Messages.Select(m => m.Message)));
        var link = ResumeLink.Match(text);
        if (!link.Success)
        {
            throw new InteropEnvironmentException(
                $"The kit's handshake test {waiting.TestId} is waiting without a confirmation link in its message.");
        }

        await Task.Delay(HandshakeAllowance, ct);

        var kitRoot = kitApiBaseUrl.TrimEnd('/');
        if (kitRoot.EndsWith("/api", StringComparison.Ordinal)) kitRoot = kitRoot[..^4];
        var resume = new Uri(link.Groups["url"].Value);
        // The kit answers with a redirect to its own UI, on its in-network
        // name; the resume is done by then, so the redirect is not followed.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = InteropSettings.Timeouts.ProtocolCall,
        };
        using var response = await http.GetAsync($"{kitRoot}{resume.PathAndQuery}", ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (>= 300 and < 400))
        {
            throw new InteropEnvironmentException(
                $"The kit answered HTTP {(int)response.StatusCode} to the handshake confirmation link.");
        }
    }
}
