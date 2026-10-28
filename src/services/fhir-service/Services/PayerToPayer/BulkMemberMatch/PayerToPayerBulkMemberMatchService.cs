using System.Text.Json.Nodes;
using FhirService.Models.PayerToPayer;
using FhirService.Services.BulkExport;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.BulkMemberMatch;

/// <summary>Who asks for a <c>$bulk-member-match</c>, decided by the controller from the token.</summary>
public sealed record BulkMemberMatchCaller(string TenantId, string ReceivingPayerId, string RequestedBy);

/// <summary>The answer: the Parameters PDex returns, and the request id its Groups share.</summary>
public sealed record BulkMemberMatchAnswer(string RequestId, JsonObject Parameters);

/// <summary>
/// PDex <c>$bulk-member-match</c> (G-35): many members matched at once, each
/// exactly as the single <c>$member-match</c> matches it, and sorted into the
/// Groups PDex defines.
/// </summary>
public interface IPayerToPayerBulkMemberMatchService
{
    Task<BulkMemberMatchAnswer> MatchAsync(
        BulkMemberMatchCaller caller, IReadOnlyList<BulkMemberMatchEntry> members, CancellationToken ct = default);

    /// <summary>The answer to an earlier request, when it belongs to this caller and has not expired.</summary>
    Task<JsonObject?> GetAnswerAsync(BulkMemberMatchCaller caller, string requestId, CancellationToken ct = default);

    /// <summary>One of the caller's Groups, when it has not expired.</summary>
    Task<PayerToPayerMatchGroup?> GetGroupAsync(BulkMemberMatchCaller caller, string groupId, CancellationToken ct = default);
}

/// <summary>
/// Matches each member through <see cref="IPayerToPayerBulkMemberMatcher"/>
/// (the same identity rules, sufficiency gate and consent checks as
/// <c>$member-match</c>; a member's consent is recorded for the payer only
/// when it is accepted), then answers with the PDex 2.1 Groups:
///
/// <list type="bullet">
///   <item><c>MatchedMembers</c> (always): each member as <c>Patient/{CHO member id}</c>,
///   carrying the <c>base-ext-match-parameters</c> extension to the submitted
///   Patient, contained;</item>
///   <item><c>NonMatchedMembers</c>: the submitted Patients CHO could not
///   resolve to exactly one member and coverage, contained and referenced;</item>
///   <item><c>ConsentConstrainedMembers</c>: members CHO matched whose consent
///   it cannot honour, said no more about than that.</item>
/// </list>
///
/// Every Group names the requesting payer in its <c>match</c> characteristic
/// and this plan as managing entity, and is kept for
/// <see cref="BulkExportOptions.MatchGroupLifetime"/> so the payer can read it
/// and export the matched one.
/// </summary>
public sealed class PayerToPayerBulkMemberMatchService : IPayerToPayerBulkMemberMatchService
{
    public const string ResultSystem = "http://hl7.org/fhir/us/davinci-pdex/CodeSystem/PdexMultiMemberMatchResultCS";
    public const string MatchParametersExtension = "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/base-ext-match-parameters";
    public const string MatchGroupProfile = "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-member-match-group";
    public const string NoMatchGroupProfile = "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-member-no-match-group";

    public const string Matched = "match";
    public const string NotMatched = "nomatch";
    public const string ConsentConstrained = "consentconstraint";

    private readonly IPayerToPayerBulkMemberMatcher _matcher;
    private readonly IPayerToPayerMatchGroupStore _groups;
    private readonly BulkExportOptions _options;
    private readonly string _localPayerId;
    private readonly TimeProvider _time;

    public PayerToPayerBulkMemberMatchService(
        IPayerToPayerBulkMemberMatcher matcher,
        IPayerToPayerMatchGroupStore groups,
        IOptions<BulkExportOptions> options,
        IOptions<Outbound.PayerToPayerDirectoryOptions> directory,
        TimeProvider time)
    {
        _matcher = matcher;
        _groups = groups;
        _options = options.Value;
        _localPayerId = directory.Value.LocalPayerId;
        _time = time;
    }

    public async Task<BulkMemberMatchAnswer> MatchAsync(
        BulkMemberMatchCaller caller, IReadOnlyList<BulkMemberMatchEntry> members, CancellationToken ct = default)
    {
        var buckets = new Dictionary<string, List<(BulkMemberMatchEntry Entry, string? MemberId)>>
        {
            [Matched] = [], [NotMatched] = [], [ConsentConstrained] = [],
        };

        foreach (var entry in members)
        {
            var result = await _matcher.MatchMemberOfBulkAsync(new MemberMatchRequest
            {
                TenantId = caller.TenantId,
                ReceivingPayerId = caller.ReceivingPayerId,
                InitiatedBy = $"client:{caller.RequestedBy}",
                FamilyName = entry.FamilyName,
                GivenName = entry.GivenName,
                BirthDate = entry.BirthDate,
                Gender = entry.Gender,
                MemberId = entry.MemberId,
                PostalCode = entry.PostalCode,
                Phone = entry.Phone,
                Email = entry.Email,
                AsOfDate = entry.AsOfDate,
                Consent = entry.Consent,
            }, ct);

            var bucket = result.Outcome switch
            {
                MemberMatchOutcome.Matched => Matched,
                MemberMatchOutcome.ConsentNotAccepted => ConsentConstrained,
                _ => NotMatched,
            };
            buckets[bucket].Add((entry, result.Outcome == MemberMatchOutcome.Matched ? result.MatchedMemberId : null));
        }

        var now = _time.GetUtcNow();
        var requestId = Guid.NewGuid().ToString("N");
        var groups = new List<PayerToPayerMatchGroup>();
        foreach (var code in new[] { Matched, NotMatched, ConsentConstrained })
        {
            // MatchedMembers is always answered (1..1), even when empty; the
            // others only when they hold someone (0..1).
            if (code != Matched && buckets[code].Count == 0)
                continue;

            var groupId = Guid.NewGuid().ToString("N");
            var memberIds = buckets[code].Where(m => m.MemberId is not null).Select(m => m.MemberId!)
                .Distinct(StringComparer.Ordinal).ToList();
            groups.Add(new PayerToPayerMatchGroup
            {
                TenantId = caller.TenantId,
                GroupId = groupId,
                RequestId = requestId,
                Code = code,
                ReceivingPayerId = caller.ReceivingPayerId,
                RequestedBy = caller.RequestedBy,
                MemberIds = memberIds,
                Resource = BuildGroup(groupId, code, buckets[code], caller.ReceivingPayerId, now).ToJsonString(),
                CreatedAt = now,
                ExpiresAt = now + _options.MatchGroupLifetime,
            });
        }

        await _groups.SaveAsync(groups, ct);
        return new BulkMemberMatchAnswer(requestId, BuildParameters(groups));
    }

    public async Task<JsonObject?> GetAnswerAsync(BulkMemberMatchCaller caller, string requestId, CancellationToken ct = default)
    {
        var groups = (await _groups.ListForRequestAsync(caller.TenantId, requestId, _time.GetUtcNow(), ct))
            .Where(g => Owns(caller, g))
            .ToList();
        return groups.Any(g => g.Code == Matched) ? BuildParameters(groups) : null;
    }

    public async Task<PayerToPayerMatchGroup?> GetGroupAsync(
        BulkMemberMatchCaller caller, string groupId, CancellationToken ct = default)
    {
        var group = await _groups.GetAsync(caller.TenantId, groupId, _time.GetUtcNow(), ct);
        return group is not null && Owns(caller, group) ? group : null;
    }

    /// <summary>A Group is the requesting payer's, and only the client that asked may use it.</summary>
    internal static bool Owns(BulkMemberMatchCaller caller, PayerToPayerMatchGroup group)
        => string.Equals(group.ReceivingPayerId, caller.ReceivingPayerId, StringComparison.Ordinal)
           && string.Equals(group.RequestedBy, caller.RequestedBy, StringComparison.Ordinal);

    private static JsonObject BuildParameters(IEnumerable<PayerToPayerMatchGroup> groups)
    {
        var parameters = new JsonArray();
        foreach (var (code, name) in new[]
                 {
                     (Matched, "MatchedMembers"), (NotMatched, "NonMatchedMembers"),
                     (ConsentConstrained, "ConsentConstrainedMembers"),
                 })
        {
            var group = groups.FirstOrDefault(g => g.Code == code);
            if (group is not null)
                parameters.Add(new JsonObject { ["name"] = name, ["resource"] = JsonNode.Parse(group.Resource) });
        }

        return new JsonObject
        {
            ["resourceType"] = "Parameters",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray(
                    "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/pdex-parameters-multi-member-match-bundle-out"),
            },
            ["parameter"] = parameters,
        };
    }

    private JsonObject BuildGroup(
        string groupId, string code, List<(BulkMemberMatchEntry Entry, string? MemberId)> members,
        string receivingPayerId, DateTimeOffset now)
    {
        var contained = new JsonArray();
        var entries = new JsonArray();
        for (var i = 0; i < members.Count; i++)
        {
            // The submitted Patient as it came, under a local id unique in this
            // Group (submitted ids are not), without the meta base FHIR forbids
            // on a contained resource.
            var localId = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var patient = (JsonObject)members[i].Entry.MemberPatient.DeepClone();
            patient["id"] = localId;
            if (patient["meta"] is JsonObject meta)
            {
                meta.Remove("versionId");
                meta.Remove("lastUpdated");
                meta.Remove("security");
                if (meta.Count == 0) patient.Remove("meta");
            }
            contained.Add(patient);

            entries.Add(new JsonObject
            {
                ["entity"] = new JsonObject
                {
                    ["extension"] = new JsonArray(new JsonObject
                    {
                        ["url"] = MatchParametersExtension,
                        ["valueReference"] = new JsonObject { ["reference"] = $"#{localId}" },
                    }),
                    // A matched member is CHO's Patient; anyone else is only the
                    // Patient that was submitted.
                    ["reference"] = code == Matched ? $"Patient/{members[i].MemberId}" : $"#{localId}",
                },
            });
        }

        var display = code switch
        {
            Matched => "Matched",
            NotMatched => "Not Matched",
            _ => "Consent Constraint",
        };

        var group = new JsonObject
        {
            ["resourceType"] = "Group",
            ["id"] = groupId,
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray(code == Matched ? MatchGroupProfile : NoMatchGroupProfile),
            },
            ["contained"] = contained,
            ["type"] = "person",
            ["actual"] = true,
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = ResultSystem, ["code"] = code, ["display"] = display }),
            },
            ["characteristic"] = new JsonArray(new JsonObject
            {
                ["code"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject { ["system"] = ResultSystem, ["code"] = Matched }),
                },
                // The payer the Group was made for.
                ["valueReference"] = new JsonObject
                {
                    ["identifier"] = new JsonObject { ["value"] = receivingPayerId },
                },
                ["exclude"] = false,
                ["period"] = new JsonObject
                {
                    ["start"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                },
            }),
            ["quantity"] = members.Count,
            ["managingEntity"] = new JsonObject
            {
                ["identifier"] = new JsonObject { ["value"] = _localPayerId },
            },
        };
        if (contained.Count == 0) group.Remove("contained");
        if (entries.Count > 0) group["member"] = entries;
        return group;
    }
}
