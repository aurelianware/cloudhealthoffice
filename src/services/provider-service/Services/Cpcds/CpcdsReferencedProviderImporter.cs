using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Import.Cpcds;
using ProviderService.Models;
using ProviderService.Repositories;

namespace ProviderService.Services.Cpcds;

/// <summary>
/// Makes the providers a CPCDS claim extract names resolvable. A CPCDS claim
/// file carries provider NPIs but no names, and the CARIN BB EOBs built from
/// it reference <c>Practitioner/{npi}</c> (care team) and, where the type is
/// known, <c>Organization/{npi}</c>. For a plan whose providers live in its
/// core, those references would 404. This reads the distinct NPIs from the
/// claim file, leaves alone every NPI CHO already has, and creates the rest
/// as <see cref="Provider.ReferenceOnly"/> providers described from NPPES:
/// readable through the FHIR API, never published in the public directory.
/// </summary>
public sealed class CpcdsReferencedProviderImporter
{
    private static readonly Regex NpiPattern = new(@"^\d{10}$", RegexOptions.Compiled);

    /// <summary>The claim-file columns that name a provider, by CPCDS name (and aliases).</summary>
    internal static readonly IReadOnlyList<(string Role, string[] Names)> ProviderColumns =
    [
        ("billing", ["Claim billing provider NPI"]),
        ("performing", ["Claim performing provider NPI", "Claim rendering provider NPI"]),
        ("attending", ["Claim attending provider NPI"]),
        ("referring", ["Claim referring provider NPI"]),
        ("site of service", ["Claim site of service NPI"]),
    ];

    private readonly IProviderRepository _providers;
    private readonly IProviderVersioningService _versioning;
    private readonly INppesLookupClient _nppes;

    public CpcdsReferencedProviderImporter(
        IProviderRepository providers, IProviderVersioningService versioning, INppesLookupClient nppes)
    {
        _providers = providers;
        _versioning = versioning;
        _nppes = nppes;
    }

    public async Task<CpcdsProviderImportResult> ImportAsync(
        string tenantId,
        string claimFileContent,
        IReadOnlyDictionary<string, string>? columnMap,
        bool dryRun,
        string actorId,
        CancellationToken ct = default)
    {
        var result = new CpcdsProviderImportResult { DryRun = dryRun };
        var npis = ReadNpis(claimFileContent, columnMap, result);
        if (result.FileErrors.Count > 0)
            return result;

        foreach (var (npi, roles) in npis)
        {
            ct.ThrowIfCancellationRequested();
            var entry = new CpcdsProviderResult { Npi = npi, Roles = roles.Order(StringComparer.Ordinal).ToList() };
            result.Providers.Add(entry);

            if (!NpiPattern.IsMatch(npi))
            {
                entry.Outcome = CpcdsProviderOutcome.Invalid;
                entry.Message = "Not a 10-digit NPI.";
                continue;
            }

            var existing = await _providers.GetByNPIAsync(npi);
            if (existing is not null)
            {
                entry.Outcome = CpcdsProviderOutcome.AlreadyKnown;
                entry.ProviderType = existing.ProviderType;
                continue;
            }

            NppesRecord? nppes;
            try
            {
                nppes = await _nppes.LookupAsync(tenantId, npi, ct);
            }
            catch (NppesUnavailableException ex)
            {
                entry.Outcome = CpcdsProviderOutcome.LookupFailed;
                entry.Message = ex.Message + " Reload the file to retry.";
                continue;
            }

            if (nppes is null)
            {
                entry.Outcome = CpcdsProviderOutcome.NotInNppes;
                entry.Message = "NPPES has no such NPI; EOBs naming it carry the NPI only.";
                continue;
            }

            var provider = FromNppes(tenantId, nppes);
            entry.ProviderType = provider.ProviderType;
            entry.Name = provider.ProviderType == ProviderType.Individual
                ? $"{provider.FirstName} {provider.LastName}".Trim()
                : provider.OrganizationName;
            if (nppes.NpiStatus == NppesRecordStatus.Deactivated)
                entry.Message = "NPPES lists this NPI as deactivated; created so older EOBs still resolve.";

            entry.Outcome = CpcdsProviderOutcome.Created;
            if (dryRun)
                continue;

            var draft = await _versioning.CreateDraftAsync(provider, actorId);
            var active = await _versioning.ActivateVersionAsync(draft.ProviderId, draft.VersionId, actorId);
            entry.ProviderId = active.ProviderId;
        }

        return result;
    }

    /// <summary>Distinct NPIs in the provider columns, with the roles each appears in.</summary>
    internal static Dictionary<string, HashSet<string>> ReadNpis(
        string content, IReadOnlyDictionary<string, string>? columnMap, CpcdsProviderImportResult result)
    {
        var npis = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            result.FileErrors.Add("The file is empty.");
            return npis;
        }

        var delimiter = CpcdsDelimitedReader.DetectDelimiter(content);
        using var records = CpcdsDelimitedReader.Read(content, delimiter).GetEnumerator();
        if (!records.MoveNext())
        {
            result.FileErrors.Add("The file has no header row.");
            return npis;
        }

        var headerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = records.Current.Fields;
        for (var i = 0; i < header.Count; i++)
            headerIndex.TryAdd(CpcdsHeaders.Normalize(header[i]), i);

        var mapped = (columnMap ?? new Dictionary<string, string>())
            .ToDictionary(kv => CpcdsHeaders.Normalize(kv.Key), kv => CpcdsHeaders.Normalize(kv.Value), StringComparer.Ordinal);

        var columns = new List<(string Role, int Index)>();
        foreach (var (role, names) in ProviderColumns)
        {
            foreach (var name in names)
            {
                var key = CpcdsHeaders.Normalize(name);
                if (mapped.TryGetValue(key, out var fileHeader))
                    key = fileHeader;
                if (headerIndex.TryGetValue(key, out var index))
                {
                    columns.Add((role, index));
                    break;
                }
            }
        }

        if (columns.Count == 0)
        {
            result.FileErrors.Add(
                "No provider NPI column found; expected at least \"Claim billing provider NPI\" " +
                "(use columnMap if the file names it differently).");
            return npis;
        }

        while (records.MoveNext())
        {
            result.RowsRead++;
            var fields = records.Current.Fields;
            foreach (var (role, index) in columns)
            {
                var npi = index < fields.Count ? fields[index].Trim() : string.Empty;
                if (npi.Length == 0) continue;
                if (!npis.TryGetValue(npi, out var roles))
                    npis[npi] = roles = new HashSet<string>(StringComparer.Ordinal);
                roles.Add(role);
            }
        }

        return npis;
    }

    internal static Provider FromNppes(string tenantId, NppesRecord nppes)
    {
        var individual = nppes.EnumerationType == NppesEntityType.Individual;
        var taxonomy = nppes.Taxonomies.FirstOrDefault(t => t.IsPrimary) ?? nppes.Taxonomies.FirstOrDefault();
        var address = nppes.Addresses.FirstOrDefault(a => string.Equals(a.AddressPurpose, "LOCATION", StringComparison.OrdinalIgnoreCase))
                      ?? nppes.Addresses.FirstOrDefault();

        return new Provider
        {
            TenantId = tenantId,
            NPI = nppes.Npi,
            ProviderType = individual ? ProviderType.Individual : ProviderType.Organization,
            FirstName = individual ? nppes.ProviderFirstName : null,
            LastName = individual ? nppes.ProviderLastName : null,
            MiddleName = individual ? nppes.ProviderMiddleName : null,
            Credentials = individual ? nppes.ProviderCredential : null,
            OrganizationName = individual ? null : nppes.OrganizationName,
            PrimarySpecialty = taxonomy?.Description ?? string.Empty,
            TaxonomyCode = taxonomy?.Code ?? string.Empty,
            Address = address is null
                ? null
                : string.Join(", ", new[] { address.AddressLine1, address.AddressLine2 }.Where(l => !string.IsNullOrWhiteSpace(l))),
            City = address?.City,
            State = address?.State,
            ZipCode = address?.PostalCode,
            Phone = address?.TelephoneNumber,
            AcceptingNewPatients = false,
            ReferenceOnly = true,
        };
    }
}

public enum CpcdsProviderOutcome
{
    /// <summary>CHO already has a provider with this NPI; left as it is.</summary>
    AlreadyKnown,

    /// <summary>Created (or, on a dry run, would be) as a reference-only provider from NPPES.</summary>
    Created,

    /// <summary>NPPES has no such NPI.</summary>
    NotInNppes,

    /// <summary>The NPPES lookup failed; a reload retries it.</summary>
    LookupFailed,

    /// <summary>The value is not an NPI.</summary>
    Invalid,
}

public sealed class CpcdsProviderResult
{
    public string Npi { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
    public CpcdsProviderOutcome Outcome { get; set; }
    public ProviderType? ProviderType { get; set; }
    public string? Name { get; set; }
    public string? ProviderId { get; set; }
    public string? Message { get; set; }
}

public sealed class CpcdsProviderImportResult
{
    public bool DryRun { get; set; }
    public int RowsRead { get; set; }
    public List<string> FileErrors { get; } = [];
    public List<CpcdsProviderResult> Providers { get; } = [];

    public int NpisInFile => Providers.Count;
    public int AlreadyKnown => Providers.Count(p => p.Outcome == CpcdsProviderOutcome.AlreadyKnown);
    public int Created => Providers.Count(p => p.Outcome == CpcdsProviderOutcome.Created);
    public int NotInNppes => Providers.Count(p => p.Outcome == CpcdsProviderOutcome.NotInNppes);
    public int LookupFailed => Providers.Count(p => p.Outcome == CpcdsProviderOutcome.LookupFailed);
    public int Invalid => Providers.Count(p => p.Outcome == CpcdsProviderOutcome.Invalid);
}
