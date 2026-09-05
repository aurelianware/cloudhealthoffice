using System.Text.Json;
using FhirService.Models.PayerToPayer;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace FhirService.Services.PayerToPayer.Outbound;

/// <summary>The remote payer's identity answer, as CHO reads it.</summary>
public sealed record RemoteMatchReading(bool IsValid, string? RemoteMemberId, string? RemoteCoverageId)
{
    public static readonly RemoteMatchReading Invalid = new(false, null, null);
}

/// <summary>Why a received package was rejected (or that it was accepted).</summary>
public enum PackageValidationOutcome
{
    Valid,

    /// <summary>The payload did not parse, or was not a FHIR Bundle.</summary>
    NotABundle,

    /// <summary>The Bundle carried no resources.</summary>
    Empty,

    /// <summary>The Bundle carried no Patient, or more than one.</summary>
    NoSingleMember,

    /// <summary>The Bundle's Patient is not the member the match resolved.</summary>
    MemberMismatch,

    /// <summary>A resource in the Bundle references a different patient.</summary>
    ForeignPatientReference,
}

/// <summary>Result of validating a received package.</summary>
public sealed record PackageValidation(PackageValidationOutcome Outcome, Bundle? Bundle, int ResourceCount)
{
    public bool IsValid => Outcome == PackageValidationOutcome.Valid && Bundle is not null;

    public static PackageValidation Rejected(PackageValidationOutcome outcome) => new(outcome, null, 0);
}

/// <summary>
/// Reads and validates what a remote payer sent back. Nothing a peer returns is
/// trusted: the payload is parsed with the FHIR R4 model (a malformed or
/// non-Bundle body is rejected, never partially consumed), and the package must
/// be consistent with the member the match resolved before CHO will treat the
/// exchange as complete.
///
/// The checks are deliberately the few that matter for member safety rather than
/// a general profile-validation framework:
///   * the payload parses as a FHIR Bundle and carries resources;
///   * it contains exactly one Patient, and that Patient is the matched member;
///   * every <c>Patient/…</c> reference anywhere in the package points at that
///     same member — so another member's claims cannot ride along.
/// </summary>
public static class PayerToPayerResponseReader
{
    /// <summary>
    /// Structural FHIR R4 parsing of a peer's payload. Element validation is
    /// deliberately off: Cloud Health Office is not the conformance validator for
    /// another payer's data, and rejecting a member's record because a profile
    /// element is thin would lose data the member is entitled to. What CHO does
    /// enforce — that the package parses at all, and that every resource in it
    /// belongs to the matched member — is applied explicitly below.
    /// </summary>
    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(
            ModelInfo.ModelInspector, new FhirJsonPocoDeserializerSettings { Validator = null });

    /// <summary>
    /// Reads a remote <c>$member-match</c> payload: a Bundle carrying the matched
    /// Patient (and optionally the Coverage context), which is the shape Cloud
    /// Health Office itself returns for the operation.
    /// </summary>
    public static RemoteMatchReading ReadMatch(string? payload)
    {
        var bundle = TryParseBundle(payload);
        if (bundle is null) return RemoteMatchReading.Invalid;

        var patients = ResourcesOf<Patient>(bundle);
        if (patients.Count != 1) return RemoteMatchReading.Invalid;

        var memberId = patients[0].Id;
        if (string.IsNullOrWhiteSpace(memberId)) return RemoteMatchReading.Invalid;

        var coverageId = ResourcesOf<Coverage>(bundle).FirstOrDefault()?.Id;
        return new RemoteMatchReading(true, memberId, coverageId);
    }

    /// <summary>
    /// Validates a received member-data package against the member the match
    /// resolved.
    /// </summary>
    public static PackageValidation ValidatePackage(string? payload, string expectedRemoteMemberId)
    {
        var bundle = TryParseBundle(payload);
        if (bundle is null) return PackageValidation.Rejected(PackageValidationOutcome.NotABundle);

        var resources = bundle.Entry?.Where(e => e.Resource is not null).Select(e => e.Resource!).ToList()
            ?? new List<Resource>();
        if (resources.Count == 0) return PackageValidation.Rejected(PackageValidationOutcome.Empty);

        var patients = resources.OfType<Patient>().ToList();
        if (patients.Count != 1) return PackageValidation.Rejected(PackageValidationOutcome.NoSingleMember);

        var patientId = patients[0].Id;
        if (string.IsNullOrWhiteSpace(patientId)
            || !string.Equals(patientId, expectedRemoteMemberId, StringComparison.Ordinal))
            return PackageValidation.Rejected(PackageValidationOutcome.MemberMismatch);

        foreach (var resource in resources)
        {
            foreach (var reference in PatientReferences(resource))
            {
                if (!IsTheMatchedMember(reference, expectedRemoteMemberId))
                    return PackageValidation.Rejected(PackageValidationOutcome.ForeignPatientReference);
            }
        }

        return new PackageValidation(PackageValidationOutcome.Valid, bundle, resources.Count);
    }

    /// <summary>
    /// Stamps the received Bundle with a <c>Provenance</c> naming the payer it
    /// came from, the exchange, and when it arrived — so downstream consumers can
    /// never mistake another payer's data for CHO-originated data. The remote
    /// endpoint is identified by its directory key, never by URL.
    /// </summary>
    public static Bundle StampProvenance(Bundle bundle, PayerToPayerSourceProvenance provenance)
    {
        var targets = bundle.Entry?
            .Where(e => e.Resource is not null)
            .Select(e => new ResourceReference($"{e.Resource!.TypeName}/{e.Resource.Id}"))
            .ToList() ?? new List<ResourceReference>();

        var stamp = new Provenance
        {
            Id = $"p2p-source-{provenance.ExchangeId}",
            Target = targets,
            Recorded = provenance.ReceivedAtUtc,
            Agent =
            [
                new Provenance.AgentComponent
                {
                    Type = new CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/provenance-participant-type",
                        "custodian",
                        provenance.SourcePayerId),
                    Who = new ResourceReference { Display = provenance.SourcePayerId },
                },
            ],
        };

        var entries = new List<Bundle.EntryComponent>(bundle.Entry ?? new List<Bundle.EntryComponent>())
        {
            new() { FullUrl = $"Provenance/{stamp.Id}", Resource = stamp },
        };
        bundle.Entry = entries;
        return bundle;
    }

    private static Bundle? TryParseBundle(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<Bundle>(payload, FhirJson);
        }
        catch (Exception ex) when (ex is DeserializationFailedException or JsonException
                                      or FormatException or ArgumentException or NotSupportedException
                                      or InvalidCastException)
        {
            // A peer's payload is untrusted input: any parse failure is a rejected
            // response, never an exception that escapes the workflow.
            return null;
        }
    }

    private static List<T> ResourcesOf<T>(Bundle bundle) where T : Resource =>
        bundle.Entry?.Select(e => e.Resource).OfType<T>().ToList() ?? new List<T>();

    /// <summary>
    /// Every Patient reference anywhere inside a resource (Coverage beneficiary,
    /// EOB patient, and any nested element), walked over the FHIR model rather
    /// than a fixed list of properties so a new resource type cannot smuggle a
    /// foreign patient reference past the check. Both relative
    /// (<c>Patient/123</c>) and absolute (<c>https://peer/fhir/Patient/123</c>)
    /// forms are collected — an absolute URL must not be a way around the check.
    /// </summary>
    private static IEnumerable<string> PatientReferences(Base element)
    {
        if (element is ResourceReference { Reference: { } reference }
            && reference.Contains("Patient/", StringComparison.Ordinal))
        {
            yield return reference;
        }

        foreach (var child in element.Children)
        {
            foreach (var nested in PatientReferences(child))
                yield return nested;
        }
    }

    /// <summary>
    /// True when a Patient reference names the matched member. The id is taken
    /// from the last <c>Patient/</c> segment (so a relative and an absolute
    /// reference are judged the same way) with any <c>_history</c> suffix
    /// removed; anything that does not yield exactly the matched id is foreign.
    /// </summary>
    private static bool IsTheMatchedMember(string reference, string expectedRemoteMemberId)
    {
        var marker = reference.LastIndexOf("Patient/", StringComparison.Ordinal);
        if (marker < 0) return false;

        var id = reference[(marker + "Patient/".Length)..];
        var historyAt = id.IndexOf("/_history/", StringComparison.Ordinal);
        if (historyAt >= 0) id = id[..historyAt];

        return string.Equals(id, expectedRemoteMemberId, StringComparison.Ordinal);
    }
}
