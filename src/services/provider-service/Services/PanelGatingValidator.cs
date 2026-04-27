using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Options;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Soft-validation telemetry for the network-participation panel-gating
/// contract (capability 5.5). Producers that elide the five panel-gating
/// fields on a write produce a structured warning log + Prometheus
/// counter increment so the follow-up hard-validation cutover can flip
/// on telemetry-driven evidence.
///
/// <para>
/// Soft validation accepts the write — no 400 is returned. Hard
/// validation lands in a follow-up PR once telemetry shows zero
/// soft-warning producers for a sustained window. See
/// <c>docs/architecture/network-participation-backfill.md</c> for the
/// transition plan.
/// </para>
/// </summary>
public interface IPanelGatingValidator
{
    /// <summary>
    /// Inspects each <see cref="NetworkParticipation"/> on the supplied
    /// provider write and emits a soft-validation warning per
    /// participation that has all five panel-gating fields at type
    /// defaults. Idempotent — calling twice produces twice the
    /// telemetry, so call exactly once per write surface.
    /// </summary>
    void Inspect(string callSite, string tenantId, Provider provider);

    /// <summary>
    /// Inspects a single <see cref="NetworkParticipation"/> on a write
    /// surface that appends one row at a time
    /// (<c>AddNetworkParticipation</c>).
    /// </summary>
    void Inspect(string callSite, string tenantId, Provider provider, NetworkParticipation participation);
}

public sealed class PanelGatingValidator : IPanelGatingValidator
{
    private readonly ILogger<PanelGatingValidator> _logger;
    private readonly IOptionsMonitor<NetworkParticipationBackfillOptions> _options;

    public PanelGatingValidator(
        ILogger<PanelGatingValidator> logger,
        IOptionsMonitor<NetworkParticipationBackfillOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public void Inspect(string callSite, string tenantId, Provider provider)
    {
        if (provider == null) return;
        var participations = provider.NetworkParticipations;
        if (participations == null || participations.Count == 0) return;

        for (var i = 0; i < participations.Count; i++)
        {
            EmitIfMissing(callSite, tenantId, provider, participations[i], i);
        }
    }

    public void Inspect(string callSite, string tenantId, Provider provider, NetworkParticipation participation)
    {
        if (provider == null) return;
        if (participation == null) return;

        // Index unknown for this surface; Add appends to the end of the
        // existing array — the resulting index is Count-1 if the caller
        // already mutated the array, or unknown otherwise. Use -1 as a
        // sentinel so dashboards can distinguish "added" from "edited
        // by index". Guarded against null NetworkParticipations so a
        // caller passing a freshly-constructed Provider without
        // initialising the list doesn't throw NullReferenceException.
        var participations = provider.NetworkParticipations;
        var index = participations == null ? -1 : participations.LastIndexOf(participation);
        EmitIfMissing(callSite, tenantId, provider, participation, index);
    }

    private void EmitIfMissing(
        string callSite,
        string tenantId,
        Provider provider,
        NetworkParticipation participation,
        int index)
    {
        if (!PanelGatingFields.IsAtTypeDefaults(participation)) return;

        var level = _options.CurrentValue.SoftValidationLogLevel;
        // The structured log payload is the source of truth for the
        // hard-validation cutover decision — every field a follow-up
        // dashboard might filter on must appear here.
        _logger.Log(level,
            "PanelGatingFieldsMissing on participation write. caller={Caller} tenantId={Tenant} providerId={ProviderId} npi={NPI} participationIndex={Index} planId={PlanId} networkId={NetworkId} lineOfBusiness={LOB}",
            Sanitize(callSite),
            Sanitize(tenantId),
            Sanitize(provider.ProviderId),
            Sanitize(provider.NPI),
            index,
            Sanitize(participation.PlanId),
            Sanitize(participation.NetworkId),
            participation.LineOfBusiness);

        ChoMetrics.PanelGatingMissingWrites.Add(1,
            new KeyValuePair<string, object?>("cho.caller", callSite),
            new KeyValuePair<string, object?>("cho.tenant_id", string.IsNullOrEmpty(tenantId) ? "unknown" : tenantId));
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
