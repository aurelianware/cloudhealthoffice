using FhirService.Mappers;
using FhirService.Models;
using FhirService.Models.PayerToPayer;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Assembles the standards-shaped response for a successful member-match: a FHIR
/// <c>collection</c> Bundle carrying the matched member's Patient and the
/// selected Coverage context. This is the stable CHO member/coverage identity a
/// receiving payer needs, and it is exactly what the P2P-01 export path consumes
/// — so a match need not be repeated to pull the data.
///
/// The Patient reuses the existing US Core <see cref="PatientAccessMapper"/>; the
/// Coverage is projected from the selected <see cref="ChoCoverage"/> so its
/// payer, subscriber id, status, and effective period are explicit (rather than
/// the member-derived placeholder the Patient Access projection returns).
///
/// (A formal Da Vinci HRex <c>Parameters</c>/<c>MemberIdentifier</c> wrapper is a
/// thin future addition; the resolved member/coverage identity is unchanged by
/// that envelope.)
/// </summary>
public static class PayerToPayerMemberMatchResponseBuilder
{
    private const string PayorDisplay = "Cloud Health Office Plan";

    public static FhirBundle Build(ChoMember member, ChoCoverage? coverage)
    {
        var entries = new List<FhirBundleEntry>();

        var patient = PatientAccessMapper.MapMemberToPatient(member);
        entries.Add(new FhirBundleEntry { FullUrl = $"Patient/{patient.Id}", Resource = patient });

        if (coverage is not null)
        {
            var fhirCoverage = MapCoverage(member, coverage);
            entries.Add(new FhirBundleEntry { FullUrl = $"Coverage/{fhirCoverage.Id}", Resource = fhirCoverage });
        }

        return new FhirBundle
        {
            Type = "collection",
            Total = entries.Count,
            Link = new[]
            {
                new FhirBundleLink { Relation = "self", Url = $"Patient/$member-match/{member.MemberId}" },
            },
            Entry = entries,
        };
    }

    private static FhirCoverage MapCoverage(ChoMember member, ChoCoverage coverage) => new()
    {
        Id = coverage.CoverageId ?? $"{member.MemberId}-COV",
        Meta = new FhirMeta
        {
            Profile = ["http://hl7.org/fhir/us/core/StructureDefinition/us-core-coverage"],
        },
        Status = string.IsNullOrWhiteSpace(coverage.Status) ? "active" : coverage.Status,
        Type = new FhirCodeableConcept
        {
            Coding =
            [
                new FhirCoding { System = "http://terminology.hl7.org/CodeSystem/v3-ActCode", Code = "SUBSCR" },
            ],
        },
        Beneficiary = new FhirReference { Reference = $"Patient/{member.MemberId}" },
        SubscriberId = coverage.SubscriberId ?? member.MemberId,
        Payor = [new FhirReference { Display = coverage.PayerId ?? PayorDisplay }],
        Period = coverage.PeriodStart is null && coverage.PeriodEnd is null
            ? null
            : new FhirPeriod { Start = coverage.PeriodStart, End = coverage.PeriodEnd },
    };
}
