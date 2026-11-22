using System.Globalization;
using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Import.Cpcds;
using CloudHealthOffice.Infrastructure.Programs;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services.Cpcds;

namespace ProviderService.Services.Roster;

/// <summary>
/// Loads a plan's network roster from its core: which NPIs are in which
/// network, for which plan and dates. Each row puts the provider in the network
/// (a <see cref="NetworkParticipation"/> with the network's id), creating the
/// provider from NPPES when CHO does not have it and the network when the
/// caller may. Optionally records the core's credentialing approval as a
/// delegated-authority decision. Reloading a roster adds nothing twice. See
/// <c>docs/integration/NETWORK-ROSTER-IMPORTER.md</c>.
/// </summary>
public sealed class NetworkRosterImporter(
    IProviderRepository providers,
    IProviderVersioningService versioning,
    IOrganizationService networks,
    ICredentialingService credentialing,
    INppesLookupClient nppes)
{
    public const int MaxErrors = 100;
    private const string PendingPrefix = "\u0001pending:";
    private const string DryRunPrefix = "(new) ";
    private static readonly Regex NpiPattern = new(@"^\d{10}$", RegexOptions.Compiled);
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyyMMdd", "M/d/yyyy", "MM/dd/yyyy"];

    private static readonly Dictionary<string, string> Columns = new(StringComparer.Ordinal)
    {
        ["npi"] = "npi", ["providernpi"] = "npi",
        ["network"] = "network", ["networkid"] = "network", ["networkname"] = "network",
        ["plan"] = "plan", ["planid"] = "plan", ["plancode"] = "plan",
        ["lineofbusiness"] = "lob", ["lob"] = "lob",
        ["tier"] = "tier", ["networktier"] = "tier",
        ["effectivedate"] = "effective", ["effective"] = "effective", ["startdate"] = "effective",
        ["termdate"] = "term", ["terminationdate"] = "term", ["enddate"] = "term",
        ["credentialed"] = "credentialed", ["credentialingstatus"] = "credentialed",
        ["credentialeddate"] = "credentialeddate", ["credentialingdate"] = "credentialeddate",
        ["networktype"] = "networktype",
    };

    /// <summary>What the caller may do beyond adding participations.</summary>
    public sealed record Permissions(bool CreateNetworks, bool RecordCredentialing);

    private sealed record Row(
        int Line, string Npi, string Network, string? Plan, LineOfBusiness LineOfBusiness, string Tier,
        DateTime Effective, DateTime? Term, bool? Credentialed, DateTime? CredentialedOn, NetworkType NetworkType);

    public async Task<NetworkRosterImportResult> ImportAsync(
        string tenantId, string content, LineOfBusiness defaultLineOfBusiness, Permissions may,
        bool dryRun, string actorId, string? correlationId, CancellationToken ct)
    {
        var result = new NetworkRosterImportResult { DryRun = dryRun };
        var rows = Read(content, defaultLineOfBusiness, result);
        if (result.FileErrors.Count > 0) return result;

        // Networks by id and by name (names compared ignoring case).
        var known = await ListNetworksAsync();
        var created = new Dictionary<string, Organization>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in rows.GroupBy(r => r.Npi, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var npi = group.Key;
            var provider = await providers.GetByNPIAsync(npi);
            var participations = new List<NetworkParticipation>();
            foreach (var row in group)
            {
                var network = Find(known, created, row.Network);
                if (network is null)
                {
                    if (!may.CreateNetworks)
                    {
                        Refuse(result, row, $"network '{row.Network}' does not exist (create it, or load with networks:write to create it)");
                        continue;
                    }
                    // Created only once a participation in it is saved.
                    network = new Organization
                    {
                        TenantId = tenantId,
                        OrganizationId = PendingPrefix + row.Network,
                        Name = row.Network,
                        NetworkType = row.NetworkType,
                        LineOfBusiness = row.LineOfBusiness,
                        EffectiveDate = row.Effective,
                    };
                    created[row.Network] = network;
                }

                var participation = new NetworkParticipation
                {
                    NetworkId = network.OrganizationId,
                    PlanId = row.Plan,
                    LineOfBusiness = row.LineOfBusiness,
                    NetworkTier = row.Tier,
                    EffectiveDate = row.Effective,
                    TerminationDate = row.Term,
                };
                if ((provider?.NetworkParticipations ?? []).Concat(participations).Any(p => Same(p, participation)))
                    result.Unchanged++;
                else
                    participations.Add(participation);
            }

            if (participations.Count == 0)
            {
                // Nothing new (or every row refused): the core's credentialing may still be new.
                if (provider is not null)
                    await CredentialAsync(tenantId, provider, group.ToList(), may, dryRun, actorId, correlationId, result, ct);
                continue;
            }

            if (provider is null)
            {
                NppesRecord? record;
                try
                {
                    record = await nppes.LookupAsync(tenantId, npi, ct);
                }
                catch (NppesUnavailableException ex)
                {
                    RefuseAll(result, group, $"NPPES is unavailable ({ex.Message}); reload the file to retry");
                    continue;
                }
                if (record is null)
                {
                    RefuseAll(result, group, "NPPES has no such NPI; create the provider in CHO first");
                    continue;
                }

                await MaterializeAsync(participations);
                provider = CpcdsReferencedProviderImporter.FromNppes(tenantId, record);
                provider.ReferenceOnly = false;
                provider.AcceptingNewPatients = true;
                provider.NetworkParticipations.AddRange(participations);
                result.ProvidersCreated++;
                result.ParticipationsAdded += participations.Count;
                if (!dryRun)
                {
                    var draft = await versioning.CreateDraftAsync(provider, actorId);
                    provider = await versioning.ActivateVersionAsync(draft.ProviderId, draft.VersionId, actorId);
                }
            }
            else
            {
                await MaterializeAsync(participations);
                result.ParticipationsAdded += participations.Count;
                if (provider.ReferenceOnly) result.MadeNetworkProviders++;
                if (!dryRun)
                {
                    var draft = provider.VersionState == ProviderVersionState.Draft
                        ? provider
                        : await versioning.AmendActiveProviderAsync(provider.ProviderId, actorId);
                    draft.NetworkParticipations.AddRange(participations);
                    // On the plan's roster: a network provider now, not only an NPI a claim named.
                    draft.ReferenceOnly = false;
                    draft.LastUpdatedDate = DateTime.UtcNow;
                    draft.LastUpdatedBy = actorId;
                    var updated = await providers.UpdateAsync(draft);
                    provider = await versioning.ActivateVersionAsync(updated.ProviderId, updated.VersionId, actorId);
                }
            }

            await CredentialAsync(tenantId, provider, group.ToList(), may, dryRun, actorId, correlationId, result, ct);
        }

        return result;

        // Creates the new networks these participations name (a dry run only reports them).
        async Task MaterializeAsync(List<NetworkParticipation> participations)
        {
            foreach (var participation in participations)
            {
                if (participation.NetworkId is not { } id || !id.StartsWith(PendingPrefix, StringComparison.Ordinal)) continue;
                var name = id[PendingPrefix.Length..];
                var network = created[name];
                if (network.OrganizationId.StartsWith(PendingPrefix, StringComparison.Ordinal))
                {
                    result.NetworksCreated.Add(name);
                    if (dryRun)
                    {
                        network.OrganizationId = DryRunPrefix + name;
                    }
                    else
                    {
                        var candidate = network;
                        candidate.OrganizationId = string.Empty;
                        network = await networks.CreateAndActivateAsync(candidate, actorId);
                        created[name] = network;
                    }
                }
                participation.NetworkId = network.OrganizationId;
            }
        }

        async Task<List<Organization>> ListNetworksAsync()
        {
            var all = new List<Organization>();
            for (var page = 1; page <= 50; page++)
            {
                var (items, _) = await networks.ListAsync(null, null, null, page, 200);
                all.AddRange(items);
                if (items.Count < 200) break;
            }
            return all;
        }
    }

    private async Task CredentialAsync(
        string tenantId, Provider provider, List<Row> rows, Permissions may, bool dryRun,
        string actorId, string? correlationId, NetworkRosterImportResult result, CancellationToken ct)
    {
        if (!rows.Any(r => r.Credentialed == true))
            return;
        // The provider record can say Approved with no decision behind it (a
        // provider created with credentialingStatus set). Claims read the
        // decision history (status-as-of), so only a recorded approval counts.
        if (provider.CredentialingStatus == CredentialingStatus.Approved
            && (await credentialing.GetCurrentStatusAsync(tenantId, provider.ProviderId, ct)).Status == CredentialingStatus.Approved)
            return;
        if (!may.RecordCredentialing)
        {
            if (result.Warnings.Count < MaxErrors)
                result.Warnings.Add($"{provider.NPI}: credentialed in the file, not recorded (needs providers:credential)");
            return;
        }

        // Approved from when the core says, or from the first participation,
        // so claims for earlier dates of service see it.
        var on = rows.Select(r => r.CredentialedOn).Where(d => d is not null).Min()
                 ?? rows.Min(r => r.Effective);
        result.Credentialed++;
        if (dryRun) return;
        await credentialing.RecordDecisionAsync(tenantId, provider.ProviderId, new RecordDecisionRequest
        {
            Decision = CredentialingDecision.Approved,
            DecidedAt = new DateTimeOffset(DateTime.SpecifyKind(on, DateTimeKind.Utc), TimeSpan.Zero),
            CredentialingDate = DateTime.SpecifyKind(on, DateTimeKind.Utc),
            DecisionAuthorityType = DecisionAuthorityType.DelegatedAuthority,
            DecisionAuthorityId = actorId,
        }, actorId, correlationId, ct);
    }

    private static Organization? Find(
        List<Organization> known, Dictionary<string, Organization> created, string network)
        => known.FirstOrDefault(n => string.Equals(n.OrganizationId, network, StringComparison.Ordinal))
           ?? known.FirstOrDefault(n => string.Equals(n.Name, network, StringComparison.OrdinalIgnoreCase))
           ?? created.GetValueOrDefault(network);

    private static bool Same(NetworkParticipation a, NetworkParticipation b)
        => string.Equals(a.NetworkId, b.NetworkId, StringComparison.Ordinal)
           && string.Equals(a.PlanId ?? string.Empty, b.PlanId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && a.LineOfBusiness == b.LineOfBusiness
           && a.EffectiveDate.Date == b.EffectiveDate.Date;

    private static void Refuse(NetworkRosterImportResult result, Row row, string message)
    {
        result.Rejected++;
        if (result.Errors.Count < MaxErrors) result.Errors.Add($"line {row.Line}: {message}");
    }

    private static void RefuseAll(NetworkRosterImportResult result, IEnumerable<Row> rows, string message)
    {
        foreach (var row in rows) Refuse(result, row, $"{row.Npi}: {message}");
    }

    /// <summary>The roster's rows; bad rows are reported and left out.</summary>
    private static List<Row> Read(string content, LineOfBusiness defaultLineOfBusiness, NetworkRosterImportResult result)
    {
        var rows = new List<Row>();
        if (string.IsNullOrWhiteSpace(content))
        {
            result.FileErrors.Add("The file is empty.");
            return rows;
        }

        using var records = CpcdsDelimitedReader.Read(content, CpcdsDelimitedReader.DetectDelimiter(content)).GetEnumerator();
        if (!records.MoveNext())
        {
            result.FileErrors.Add("The file is empty.");
            return rows;
        }
        var header = records.Current.Fields.Select(h => Columns.GetValueOrDefault(CpcdsHeaders.Normalize(h))).ToList();
        foreach (var (column, name) in new[] { ("npi", "NPI"), ("network", "Network"), ("effective", "Effective Date") })
        {
            if (!header.Contains(column)) result.FileErrors.Add($"The header has no {name} column.");
        }
        if (result.FileErrors.Count > 0) return rows;

        while (records.MoveNext())
        {
            var cells = records.Current.Fields;
            if (cells.All(c => c.Trim().Length == 0)) continue;
            result.RowsRead++;
            var line = records.Current.LineNumber;
            string? Cell(string column)
            {
                var at = header.IndexOf(column);
                return at >= 0 && at < cells.Count && cells[at].Trim().Length > 0 ? cells[at].Trim() : null;
            }

            var errors = new List<string>();
            var npi = Cell("npi") ?? string.Empty;
            if (!NpiPattern.IsMatch(npi)) errors.Add($"NPI '{npi}' is not 10 digits");
            var network = Cell("network");
            if (network is null) errors.Add("Network is empty");

            var effective = Date(Cell("effective"), "Effective Date", required: true, errors);
            var term = Date(Cell("term"), "Term Date", required: false, errors);
            if (effective is not null && term is not null && term <= effective)
                errors.Add("Term Date is not after Effective Date");

            var lob = defaultLineOfBusiness;
            if (Cell("lob") is { } lobText && !LineOfBusinessFrom(lobText, out lob))
                errors.Add($"Line of Business '{lobText}' is not one of {string.Join(", ", Enum.GetNames<LineOfBusiness>())}");

            var tier = Cell("tier") ?? "Tier1";
            if (int.TryParse(tier, out var level)) tier = $"Tier{level}";

            bool? credentialed = null;
            if (Cell("credentialed") is { } c)
            {
                credentialed = c.ToLowerInvariant() switch
                {
                    "y" or "yes" or "true" or "1" or "approved" or "credentialed" or "active" => true,
                    "n" or "no" or "false" or "0" or "pending" or "denied" or "not credentialed" => false,
                    _ => null,
                };
                if (credentialed is null) errors.Add($"Credentialed '{c}' is not yes or no");
            }
            var credentialedOn = Date(Cell("credentialeddate"), "Credentialed Date", required: false, errors);

            var networkType = NetworkType.PPO;
            if (Cell("networktype") is { } typeText && !Enum.TryParse(typeText, ignoreCase: true, out networkType))
                errors.Add($"Network Type '{typeText}' is not one of {string.Join(", ", Enum.GetNames<NetworkType>())}");

            if (errors.Count > 0)
            {
                result.Rejected++;
                if (result.Errors.Count < MaxErrors) result.Errors.Add($"line {line}: {string.Join("; ", errors)}");
                continue;
            }
            rows.Add(new Row(line, npi, network!, Cell("plan"), lob, tier, effective!.Value, term, credentialed, credentialedOn, networkType));
        }
        return rows;
    }

    private static DateTime? Date(string? text, string name, bool required, List<string> errors)
    {
        if (text is null)
        {
            if (required) errors.Add($"{name} is empty");
            return null;
        }
        if (DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        errors.Add($"{name} '{text}' is not a date (yyyy-MM-dd)");
        return null;
    }

    private static bool LineOfBusinessFrom(string text, out LineOfBusiness lob)
        => Enum.TryParse(text.Replace(" ", string.Empty), ignoreCase: true, out lob) && Enum.IsDefined(lob);
}

public sealed class NetworkRosterImportResult
{
    public bool DryRun { get; set; }
    public int RowsRead { get; set; }
    public int ProvidersCreated { get; set; }
    public int ParticipationsAdded { get; set; }

    /// <summary>Rows whose participation the provider already has.</summary>
    public int Unchanged { get; set; }

    /// <summary>Providers CHO knew only from a claim, now network providers.</summary>
    public int MadeNetworkProviders { get; set; }

    public int Credentialed { get; set; }
    public int Rejected { get; set; }
    public List<string> NetworksCreated { get; } = [];
    public List<string> FileErrors { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}
