using FhirService.Mappers;
using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>Assembles the member-scoped FHIR export package for an inbound respond.</summary>
public interface IPayerToPayerExportBuilder
{
    FhirBundle Build(ChoMember member, IReadOnlyList<ChoPaymentDocument> payments, PayerToPayerExchangeRequest request);
}

/// <summary>
/// Builds a deterministic FHIR <c>collection</c> Bundle for the matched member,
/// reusing the existing <see cref="PatientAccessMapper"/> (CARIN/US Core
/// projection) over CHO-owned data. The package contains the member's Patient
/// and Coverage plus the ExplanationOfBenefit resources that pass the
/// <see cref="PayerToPayerExportPolicy"/> (5-year lookback). Only the matched
/// member's data is included; references are internal and consistent.
/// </summary>
public sealed class PayerToPayerExportBuilder : IPayerToPayerExportBuilder
{
    public FhirBundle Build(
        ChoMember member, IReadOnlyList<ChoPaymentDocument> payments, PayerToPayerExchangeRequest request)
    {
        var entries = new List<FhirBundleEntry>();

        // Member demographics + coverage (US Core / CARIN projection).
        var patient = PatientAccessMapper.MapMemberToPatient(member);
        entries.Add(new FhirBundleEntry { FullUrl = $"Patient/{patient.Id}", Resource = patient });

        var coverage = PatientAccessMapper.MapMemberToCoverage(member);
        entries.Add(new FhirBundleEntry { FullUrl = $"Coverage/{coverage.Id}", Resource = coverage });

        // Member's claims payments as CARIN EOBs, filtered by the P2P policy and
        // scoped to this member. Deterministic order by payment id.
        var eobs = payments
            .Where(p => string.Equals(p.MemberId, member.MemberId, StringComparison.Ordinal))
            .Where(p => PayerToPayerExportPolicy.IncludePayment(p, request.ExchangeDateUtc, request.LookbackYears))
            .OrderBy(p => p.PaymentId, StringComparer.Ordinal)
            .Select(PatientAccessMapper.MapPaymentToEob)
            .Select(eob => new FhirBundleEntry { FullUrl = $"ExplanationOfBenefit/{eob.Id}", Resource = eob });
        entries.AddRange(eobs);

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
