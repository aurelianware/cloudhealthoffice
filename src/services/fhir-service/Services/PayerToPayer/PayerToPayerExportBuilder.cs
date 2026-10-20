using FhirService.Mappers;
using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>Assembles the member-scoped FHIR export package for an inbound respond.</summary>
public interface IPayerToPayerExportBuilder
{
    /// <summary>The export bundle for <paramref name="member"/>: the resources its export source gathered, in order.</summary>
    FhirBundle Build(ChoMember member, IReadOnlyList<FhirResource> resources, PayerToPayerExchangeRequest request);
}

public sealed class PayerToPayerExportBuilder : IPayerToPayerExportBuilder
{
    public FhirBundle Build(
        ChoMember member, IReadOnlyList<FhirResource> resources, PayerToPayerExchangeRequest request)
    {
        var entries = resources
            .Select(r => new FhirBundleEntry { FullUrl = $"{r.ResourceType}/{r.Id}", Resource = r })
            .ToList();

        return new FhirBundle
        {
            Type = "collection",
            Total = entries.Count,
            Link = new[]
            {
                new FhirBundleLink
                {
                    Relation = "self",
                    Url = $"PayerToPayer/{request.TenantId}/{member.MemberId}",
                },
            },
            Entry = entries,
        };
    }
}

/// <summary>
/// Gathers the resources the Payer-to-Payer export sends for a resolved,
/// authorized member: demographics, coverage and claims within the lookback
/// window, in a stable order.
/// </summary>
public interface IPayerToPayerExportSource
{
    Task<IReadOnlyList<FhirResource>> GetResourcesAsync(
        ChoMember member, PayerToPayerExchangeRequest request, CancellationToken ct = default);
}

/// <summary>
/// The mock export source: the member mapped to US Core Patient and Coverage,
/// and the member's payments from <see cref="IPatientAccessDataProvider"/> as
/// EOBs, filtered by <see cref="PayerToPayerExportPolicy"/>, ordered by payment id.
/// </summary>
public sealed class PatientAccessPayerToPayerExportSource : IPayerToPayerExportSource
{
    private readonly IPatientAccessDataProvider _provider;

    public PatientAccessPayerToPayerExportSource(IPatientAccessDataProvider provider) => _provider = provider;

    public async Task<IReadOnlyList<FhirResource>> GetResourcesAsync(
        ChoMember member, PayerToPayerExchangeRequest request, CancellationToken ct = default)
    {
        var resources = new List<FhirResource>
        {
            PatientAccessMapper.MapMemberToPatient(member),
            PatientAccessMapper.MapMemberToCoverage(member),
        };

        var payments = await _provider.GetPaymentsByPatientIdAsync(member.MemberId, ct);
        resources.AddRange(payments
            .Where(p => string.Equals(p.MemberId, member.MemberId, StringComparison.Ordinal))
            .Where(p => PayerToPayerExportPolicy.IncludePayment(p, request.ExchangeDateUtc, request.LookbackYears))
            .OrderBy(p => p.PaymentId, StringComparer.Ordinal)
            .Select(PatientAccessMapper.MapPaymentToEob));
        return resources;
    }
}
