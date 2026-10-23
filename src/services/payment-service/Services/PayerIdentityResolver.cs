using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Configuration;

namespace PaymentService.Services;

/// <summary>Who an 835 says paid: the payer name in N1*PR and the payer identifier beside it.</summary>
internal sealed record PayerIdentity(string Name, string Id);

/// <summary>
/// The paying entity for a run: the tenant's carrier where it states a name
/// and an 835 payer id, and configuration (<c>Payer:Name</c>, <c>Payer:Id</c>)
/// for whatever it leaves unstated (plan-configuration.md §12.3).
///
/// <para>
/// An unreachable tenant-service throws rather than answering the configured
/// values. A provider posts a remittance against the payer it names, so a run
/// that could not read the carrier is a run that does not know who is paying,
/// and failing it is better than sending 835s under the wrong payer. A carrier
/// that states nothing is a different matter: that is the deployment's own
/// identity, still correct until someone fills the carrier in.
/// </para>
/// </summary>
internal sealed class PayerIdentityResolver(ICarrierDirectory? carriers, IConfiguration configuration)
{
    /// <summary>The carrier payer-id key the 835 payer identifier is taken from.</summary>
    private const string EraTransaction = "835";

    public async Task<PayerIdentity> ResolveAsync(string tenantId, CancellationToken ct = default)
    {
        var carrier = carriers is null || string.IsNullOrWhiteSpace(tenantId)
            ? null
            : await carriers.DefaultAsync(tenantId, ct);

        return new PayerIdentity(
            carrier?.LetterheadName ?? configuration["Payer:Name"] ?? "Cloud Health Office",
            carrier?.PayerIdFor(EraTransaction) ?? configuration["Payer:Id"] ?? "CHO");
    }
}
