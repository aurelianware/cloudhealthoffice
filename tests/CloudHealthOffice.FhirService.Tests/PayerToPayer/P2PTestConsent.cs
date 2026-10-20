using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirService.Services.PayerToPayer;

namespace CloudHealthOffice.FhirService.Tests.PayerToPayer;

/// <summary>
/// Test bodies for Payer-to-Payer requests carry the member's HRex consent naming
/// the payer the token is registered to (ADR 022), as a conforming requesting
/// payer sends it. A body that already says <c>consent</c> (or <c>consent: null</c>)
/// is left alone, so consent tests stay explicit.
/// </summary>
public static class P2PTestConsent
{
    public const string LocalPayerId = "cloud-health-office";

    public static string Serialize(object body, string token)
    {
        var node = JsonSerializer.SerializeToNode(body) as JsonObject ?? new JsonObject();
        if (!node.ContainsKey("consent") && PayerOf(token) is { } payer)
            node["consent"] = For(payer);
        return node.ToJsonString();
    }

    public static JsonObject For(string payer) =>
        PdexConsentBuilder.Build("Patient/member", payer, LocalPayerId, DateTime.UtcNow.AddDays(-1), null);

    private static string? PayerOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))?["payer_id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
