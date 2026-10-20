using System.Globalization;
using System.Text.Json.Nodes;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services.ChoNative;
using FhirService.Services.PayerToPayer.Outbound;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.ChoNative;

/// <summary>
/// Turns CHO's own projections into the shapes the Payer-to-Payer policies
/// read (ADR 020). A projected Patient carries no SSN (it is redacted), so
/// SSN never contradicts or matches a CHO-native candidate.
/// </summary>
public static class ChoNativePayerToPayerMapping
{
    public static ChoMember ToChoMember(DirectoryMember member)
    {
        var patient = member.Patient;
        var name = (patient["name"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var given = (name?["given"] as JsonArray)?.Select(g => g?.GetValue<string>()).Where(g => !string.IsNullOrEmpty(g)).ToList() ?? [];
        var telecom = (patient["telecom"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var address = (patient["address"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();

        return new ChoMember
        {
            MemberId = member.MemberId,
            FirstName = given.FirstOrDefault() ?? string.Empty,
            MiddleName = given.Skip(1).FirstOrDefault(),
            LastName = name?["family"]?.GetValue<string>() ?? string.Empty,
            Dob = patient["birthDate"]?.GetValue<string>() ?? string.Empty,
            Gender = (patient["gender"]?.GetValue<string>()) switch
            {
                "male" => "M",
                "female" => "F",
                "other" => "O",
                _ => "U",
            },
            Ssn = null,
            Phone = Telecom(telecom, "phone"),
            Email = Telecom(telecom, "email"),
            Address = address is null ? null : new ChoAddress
            {
                Street1 = (address["line"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
                City = address["city"]?.GetValue<string>(),
                State = address["state"]?.GetValue<string>(),
                Zip = address["postalCode"]?.GetValue<string>(),
            },
        };
    }

    /// <summary>
    /// A coverage-service Coverage as the coverage selector reads it. The payer is
    /// the plan itself; the subscriber id is the member's subscriber, or the
    /// member's own id for a subscriber.
    /// </summary>
    public static ChoCoverage ToChoCoverage(JsonObject coverage, string memberId, string? subscriberMemberId, string localPayerId) => new()
    {
        MemberId = memberId,
        CoverageId = coverage["id"]?.GetValue<string>(),
        Status = coverage["status"]?.GetValue<string>() ?? "active",
        PayerId = localPayerId,
        SubscriberId = subscriberMemberId ?? memberId,
        PeriodStart = coverage["period"]?["start"]?.GetValue<string>(),
        PeriodEnd = coverage["period"]?["end"]?.GetValue<string>(),
    };

    private static string? Telecom(IEnumerable<JsonObject> telecom, string system) =>
        telecom.FirstOrDefault(t => t["system"]?.GetValue<string>() == system)?["value"]?.GetValue<string>();
}

/// <summary>Resolves the configured served tenant, as the mock sources do.</summary>
internal static class ServedTenant
{
    public static string From(IOptions<FhirAdapterOptions> options) =>
        string.IsNullOrWhiteSpace(options.Value.TenantId) ? "demo-tenant" : options.Value.TenantId.Trim();

    public static bool Serves(string served, string? tenantId) =>
        string.Equals(tenantId?.Trim(), served, StringComparison.Ordinal);
}

/// <summary>
/// P2P-01 member resolution over member-service: the member with the supplied
/// member id, kept only when every supplied demographic agrees.
/// </summary>
public sealed class ChoNativePayerToPayerMemberSource : IPayerToPayerMemberSource
{
    private readonly ChoNativeMemberDirectory _directory;

    public ChoNativePayerToPayerMemberSource(ChoNativeMemberDirectory directory, IOptions<FhirAdapterOptions> options)
    {
        _directory = directory;
        ServedTenantId = ServedTenant.From(options);
    }

    public string ServedTenantId { get; }

    public async Task<IReadOnlyList<ChoMember>> FindCandidatesAsync(
        string tenantId, PayerToPayerMemberCriteria criteria, CancellationToken ct = default)
    {
        if (!ServedTenant.Serves(ServedTenantId, tenantId) || string.IsNullOrWhiteSpace(criteria.MemberId))
            return Array.Empty<ChoMember>();

        var found = await _directory.FindByMemberIdAsync(tenantId.Trim(), criteria.MemberId.Trim(), ct);
        if (found is null) return Array.Empty<ChoMember>();
        var member = ChoNativePayerToPayerMapping.ToChoMember(found);
        return PayerToPayerDemographics.Agree(member, criteria) ? new[] { member } : Array.Empty<ChoMember>();
    }
}

/// <summary>
/// <c>$member-match</c> candidates over member-service (ADR 020): the member
/// with the supplied id, the members whose subscriber it is, and every member
/// born on the supplied date — a superset of every strong match except by SSN,
/// which CHO cannot search. A request with neither a member id nor a birth date
/// is therefore not searchable.
/// </summary>
public sealed class ChoNativePayerToPayerMemberMatchSource : IPayerToPayerMemberMatchSource
{
    /// <summary>Most candidates read for one path; beyond it uniqueness cannot be proven.</summary>
    public const int MaxCandidates = 500;

    private readonly ChoNativeMemberDirectory _directory;
    private readonly string _localPayerId;

    // The subscriber of each candidate this request saw, for its coverages.
    private readonly Dictionary<string, string?> _subscribers = new(StringComparer.Ordinal);

    public ChoNativePayerToPayerMemberMatchSource(
        ChoNativeMemberDirectory directory,
        IOptions<FhirAdapterOptions> options,
        IOptions<PayerToPayerDirectoryOptions> payerToPayer)
    {
        _directory = directory;
        ServedTenantId = ServedTenant.From(options);
        _localPayerId = payerToPayer.Value.LocalPayerId;
    }

    public string ServedTenantId { get; }

    public async Task<MemberMatchCandidates> GetCandidatesAsync(
        string tenantId, MemberMatchCriteria criteria, CancellationToken ct = default)
    {
        if (!ServedTenant.Serves(ServedTenantId, tenantId)) return MemberMatchCandidates.None;
        tenantId = tenantId.Trim();

        var birthDate = criteria.BirthDate is { } dob
                        && DateOnly.TryParseExact(dob, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : (DateOnly?)null;
        if (criteria.MemberId is null && birthDate is null)
            return MemberMatchCandidates.NotSearchable;

        var found = new List<DirectoryMember>();
        var complete = true;

        // The id as supplied and as normalized: stored ids keep their own
        // punctuation and case, and the policy compares normalized forms.
        var ids = new[] { criteria.SuppliedMemberId, criteria.MemberId }
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var memberId in ids)
        {
            if (await _directory.FindByMemberIdAsync(tenantId, memberId!, ct) is { } byId)
                found.Add(byId);
            var dependents = await _directory.FindBySubscriberAsync(tenantId, memberId!, MaxCandidates, ct);
            found.AddRange(dependents.Members);
            complete &= dependents.Complete;
        }

        if (birthDate is { } date)
        {
            var born = await _directory.FindByBirthDateAsync(tenantId, date, MaxCandidates, ct);
            found.AddRange(born.Members);
            complete &= born.Complete;
        }

        var distinct = found
            .GroupBy(m => m.MemberId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.MemberId, StringComparer.Ordinal)
            .ToList();
        foreach (var m in distinct) _subscribers[m.MemberId] = m.SubscriberMemberId;

        return new MemberMatchCandidates(distinct.Select(ChoNativePayerToPayerMapping.ToChoMember).ToList(), Complete: complete);
    }

    public async Task<IReadOnlyList<ChoCoverage>> GetCoveragesAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (!ServedTenant.Serves(ServedTenantId, tenantId)) return Array.Empty<ChoCoverage>();
        tenantId = tenantId.Trim();

        if (!_subscribers.TryGetValue(memberId, out var subscriber))
            subscriber = (await _directory.FindByMemberIdAsync(tenantId, memberId, ct))?.SubscriberMemberId;

        var coverages = await _directory.GetCoveragesAsync(tenantId, memberId, ct);
        return coverages
            .Select(c => ChoNativePayerToPayerMapping.ToChoCoverage(c, memberId, subscriber, _localPayerId))
            .ToList();
    }
}

/// <summary>
/// The P2P-01 export from CHO's own services (ADR 020): the Patient from
/// member-service, every Coverage from coverage-service, and the member's
/// ExplanationOfBenefit resources from claims-service whose service period
/// starts within the lookback window — each exactly as its service projects it.
/// </summary>
public sealed class ChoNativePayerToPayerExportSource : IPayerToPayerExportSource
{
    private readonly ChoNativeMemberDirectory _directory;

    public ChoNativePayerToPayerExportSource(ChoNativeMemberDirectory directory) => _directory = directory;

    public async Task<IReadOnlyList<FhirResource>> GetResourcesAsync(
        ChoMember member, PayerToPayerExchangeRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId.Trim();
        var found = await _directory.FindByMemberIdAsync(tenantId, member.MemberId, ct)
                    ?? throw new FhirUpstreamException("Patient", "Patient upstream no longer has the matched member.");

        var resources = new List<FhirResource> { new FhirJsonResource(found.Patient) };

        var coverages = await _directory.GetCoveragesAsync(tenantId, member.MemberId, ct);
        resources.AddRange(coverages
            .OrderBy(c => c["id"]?.GetValue<string>(), StringComparer.Ordinal)
            .Select(c => new FhirJsonResource(c)));

        var eobs = await _directory.GetExplanationsOfBenefitAsync(tenantId, member.MemberId, ct);
        resources.AddRange(eobs
            .Where(e => PayerToPayerExportPolicy.WithinLookback(
                e["billablePeriod"]?["start"]?.GetValue<string>(), request.ExchangeDateUtc, request.LookbackYears))
            .OrderBy(e => e["id"]?.GetValue<string>(), StringComparer.Ordinal)
            .Select(e => new FhirJsonResource(e)));

        return resources;
    }
}
