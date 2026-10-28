using System.Text.Json.Nodes;
using ProviderService.Models;

namespace ProviderService.Services;

/// <summary>
/// Projects provider-service data into the Da Vinci PDex Plan-Net 1.2.0
/// profiles served by the public Provider Directory (G-36). Builds on the
/// US Core projectors (capabilities 5.7–5.9) and reshapes their output where
/// Plan-Net differs, so the authenticated <c>/fhir/*</c> projections stay
/// exactly as they are.
/// </summary>
public interface IPlanNetDirectoryProjector
{
    /// <summary>
    /// A network as a plannet-Network Organization, managed by
    /// <paramref name="payerOrganizationId"/> (Network.partOf is required), or
    /// null when the network is not directory-eligible.
    /// </summary>
    JsonObject? ProjectNetwork(Organization network, string payerOrganizationId);

    /// <summary>
    /// Every Plan-Net resource one provider contributes to the directory:
    /// <list type="bullet">
    ///   <item>an individual: Practitioner, its practice Location, and one
    ///   PractitionerRole per active network participation;</item>
    ///   <item>an organization: Organization, its Location, a HealthcareService,
    ///   and one OrganizationAffiliation per active network participation.</item>
    /// </list>
    /// Participations whose network is not in <paramref name="networks"/> are
    /// left out, so a role never points at a network the directory does not
    /// publish. Empty when the provider is not directory-eligible.
    /// </summary>
    IReadOnlyList<JsonObject> ProjectProvider(
        Provider provider, IReadOnlyDictionary<string, Organization> networks, DateTime asOf);
}
