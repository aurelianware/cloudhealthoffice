using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;

namespace FhirService.Services.BulkExport;

/// <summary>
/// The resources a Payer-to-Payer bulk export writes for one member, as
/// <c>(resourceType, compact FHIR JSON)</c> pairs (one NDJSON line each).
/// </summary>
public interface IPayerToPayerBulkSource
{
    /// <summary>
    /// Nothing when the member is not exportable to this payer any more: no
    /// consent naming it in force, or no such member.
    /// </summary>
    IAsyncEnumerable<(string Type, string Line)> ReadAsync(
        string tenantId,
        string receivingPayerId,
        string requestedBy,
        string memberId,
        IReadOnlyCollection<string> types,
        DateTimeOffset? since,
        CancellationToken ct = default);
}

/// <summary>
/// Exports one member exactly as the single-member
/// <c>PayerToPayer/$member-data-export</c> would, through
/// <see cref="IPayerToPayerExchangeService"/>: the member resolved by id, the
/// requesting payer's consent checked again now (ADR 022), the same sources,
/// five-year claims window, prior authorizations and redaction, and the same
/// audit entry. A bulk export is many of those answers, never a wider one.
/// </summary>
public sealed class PayerToPayerBulkSource : IPayerToPayerBulkSource
{
    /// <summary>As the FHIR controllers write the lightweight records, without nulls (not valid FHIR).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPayerToPayerExchangeService _exchange;

    public PayerToPayerBulkSource(IPayerToPayerExchangeService exchange) => _exchange = exchange;

    public async IAsyncEnumerable<(string Type, string Line)> ReadAsync(
        string tenantId,
        string receivingPayerId,
        string requestedBy,
        string memberId,
        IReadOnlyCollection<string> types,
        DateTimeOffset? since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await _exchange.RespondAsync(new PayerToPayerExchangeRequest
        {
            TenantId = tenantId,
            ReceivingPayerId = receivingPayerId,
            InitiatedBy = $"client:{requestedBy}",
            MemberId = memberId,
        }, ct);

        if (result.Outcome != PayerToPayerOutcome.Exported || result.Bundle is null)
            yield break;

        foreach (var resource in (result.Bundle.Entry ?? []).Select(e => e.Resource).OfType<Models.FhirResource>())
        {
            if (JsonSerializer.SerializeToNode(resource, resource.GetType(), Json) is not JsonObject json)
                continue;
            var type = json["resourceType"]?.GetValue<string>();
            if (type is null || !types.Contains(type))
                continue;
            if (!BulkExportResourceSource.UpdatedSince(LastUpdated(json), since))
                continue;
            yield return (type, json.ToJsonString());
        }
    }

    private static DateTimeOffset? LastUpdated(JsonObject resource)
        => resource["meta"]?["lastUpdated"]?.GetValue<string>() is { } value && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
}
