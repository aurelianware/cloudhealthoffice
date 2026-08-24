using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Parses Stedi <c>transaction.processed.v2</c> webhook envelopes. The payload
/// is a pointer (transactionId), not 277CA content.
/// </summary>
internal static class StediClaimResponseEventParser
{
    public static bool TryParse(string json, out ClaimAcknowledgmentDiscovery discovery)
    {
        discovery = new ClaimAcknowledgmentDiscovery { GatewayName = StediHealthcareGateway.GatewayName };
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            discovery.EventId = GetString(root, "id");
            var detailType = GetString(root, "detail-type") ?? GetString(root, "detailType");
            if (!string.IsNullOrEmpty(detailType) &&
                !detailType.Contains("transaction.processed", StringComparison.OrdinalIgnoreCase))
            {
                discovery.TransactionSetIdentifier = "ignored";
                return true;
            }

            if (!root.TryGetProperty("detail", out var detail) || detail.ValueKind != JsonValueKind.Object)
            {
                discovery.ExternalAcknowledgmentId = GetString(root, "transactionId") ?? string.Empty;
                return !string.IsNullOrWhiteSpace(discovery.ExternalAcknowledgmentId);
            }

            discovery.ExternalAcknowledgmentId = GetString(detail, "transactionId") ?? string.Empty;
            discovery.Direction = GetString(detail, "direction");
            discovery.TransactionSetIdentifier = ReadTransactionSet(detail);
            return !string.IsNullOrWhiteSpace(discovery.ExternalAcknowledgmentId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadTransactionSet(JsonElement detail)
    {
        if (detail.TryGetProperty("x12", out var x12) && x12.ValueKind == JsonValueKind.Object)
        {
            var direct = GetString(x12, "transactionSetIdentifier");
            if (!string.IsNullOrEmpty(direct))
            {
                return direct;
            }

            if (x12.TryGetProperty("metadata", out var meta) &&
                meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("transaction", out var tx) &&
                tx.ValueKind == JsonValueKind.Object)
            {
                return GetString(tx, "transactionSetIdentifier");
            }
        }

        return GetString(detail, "transactionSetIdentifier");
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
