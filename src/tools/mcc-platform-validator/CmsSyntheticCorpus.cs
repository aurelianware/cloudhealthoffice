using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// Builds the run's corpus from CMS's public "Synthetic Medicare Enrollment,
/// Fee-for-Service Claims, and Prescription Drug Event" files (the 2023
/// release: carrier, outpatient and inpatient claims), instead of the
/// validator's own generator.
///
/// What comes from CMS: which claims exist, their type mix, lines per claim,
/// HCPCS codes and modifiers, revenue codes, units, submitted charges,
/// diagnosis codes, place of service, bill type and DRG, and which claims
/// belong to the same beneficiary. What does not: member and provider
/// identities. Each CMS beneficiary becomes one synthetic member and each CMS
/// provider number one synthetic provider from the validator's fixture
/// generator, so every NPI is valid and seeded. Service dates are re-dated
/// into recent weeks by <see cref="MccClaimDateNormalizer"/> like generated
/// claims, so plan and eligibility dates apply.
///
/// These claims carry no expected outcome, so they score as Unspecified: the
/// run measures volume, completion and throughput on real-shaped claims, not
/// correctness. CMS's guide says the files' payment amounts come from
/// Synthea's cost model and "do not closely model reality", so no payment
/// comparison is made against them.
///
/// Files are pipe-delimited text with a header row (the .csv extension
/// notwithstanding). One row is read as one claim line; rows sharing a
/// CLM_ID form one claim.
/// </summary>
public static class CmsSyntheticCorpus
{
    public sealed record LoadResult(
        List<SyntheticClaim> Claims,
        int Professional,
        int Outpatient,
        int Inpatient,
        int Beneficiaries,
        int ClaimsAvailable,
        int RowsRead,
        int ClaimsSkipped);

    private sealed record Source(string File, string Kind);

    private static readonly Source[] Sources =
    [
        new("carrier.csv", "Professional"),
        new("outpatient.csv", "Outpatient"),
        new("inpatient.csv", "Inpatient"),
    ];

    public static LoadResult Load(string directory, int maxClaims, int seed, TextWriter? log = null)
    {
        log ??= Console.Out;
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"CMS synthetic corpus directory not found: {directory}");
        }

        var refData = new InMemoryReferenceDataProvider();
        var members = new Dictionary<string, SyntheticMember>(StringComparer.Ordinal);
        var providers = new Dictionary<string, SyntheticProvider>(StringComparer.Ordinal);
        var beneficiaries = LoadBeneficiaries(directory, log);

        // Two passes over each file so only the sampled claims are ever built:
        // the full release is ~1.75M claims, and holding all of them would
        // need several times the memory of the run itself.
        var files = new List<(Source Source, string Path, List<string> Billable)>();
        var rowsRead = 0;
        var skipped = 0;
        foreach (var source in Sources)
        {
            var path = FindFile(directory, source.File);
            if (path is null)
            {
                log.WriteLine($"CMS corpus: {source.File} not found, skipped");
                continue;
            }

            var (billable, rows, skippedHere) = ScanClaimIds(path, source.Kind);
            rowsRead += rows;
            skipped += skippedHere;
            files.Add((source, path, billable));
            log.WriteLine($"CMS corpus: {Path.GetFileName(path)} has {billable.Count:N0} {source.Kind.ToLowerInvariant()} claims in {rows:N0} rows ({skippedHere:N0} with no billable line)");
        }

        var available = files.Sum(f => f.Billable.Count);
        if (available == 0)
        {
            throw new InvalidOperationException($"CMS synthetic corpus: no claims read from {directory}");
        }

        // Keep the files' own mix: take the same share of each claim type,
        // in a seeded order, so a 500K run is a fair sample of the 1.75M.
        var take = Math.Min(maxClaims, available);
        var picked = new List<(int File, string Id)>(take);
        for (var i = 0; i < files.Count; i++)
        {
            var (source, _, billable) = files[i];
            var share = (int)Math.Round((double)billable.Count / available * take);
            var prefix = Prefix(source.Kind);
            picked.AddRange(billable
                .OrderBy(id => StableHash(seed + ":" + prefix + id))
                .Take(share)
                .Select(id => (i, id)));
        }

        // Rounding can leave the total one or two off the requested count.
        if (picked.Count > take)
        {
            picked = picked.OrderBy(p => StableHash(seed + "#" + Prefix(files[p.File].Source.Kind) + p.Id)).Take(take).ToList();
        }

        var selected = new List<SyntheticClaim>(picked.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var wanted = picked.Where(p => p.File == i).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            if (wanted.Count == 0) continue;
            selected.AddRange(ReadClaims(files[i].Path, files[i].Source.Kind, wanted, refData, members, providers, beneficiaries));
        }

        selected = selected.OrderBy(c => c.ClaimId, StringComparer.Ordinal).ToList();
        var used = selected.Select(c => c.Member.MemberId).Distinct(StringComparer.Ordinal).Count();
        return new LoadResult(
            selected,
            selected.Count(c => c.ClaimType == "Professional"),
            selected.Count(c => c.ClaimType == "Institutional" && c.BillType?.StartsWith("013", StringComparison.Ordinal) == true),
            selected.Count(c => c.ClaimType == "Institutional" && c.BillType?.StartsWith("011", StringComparison.Ordinal) == true),
            used,
            available,
            rowsRead,
            skipped);
    }

    /// <summary>
    /// After <see cref="MccClaimDateNormalizer"/>: a CMS beneficiary's member is
    /// shared by all of that beneficiary's claims, and the normalizer shifts
    /// the member's coverage once per claim. Give every CMS member one plain
    /// coverage span that covers the whole re-dated window instead.
    /// </summary>
    public static void ResetMemberCoverage(IEnumerable<SyntheticClaim> claims, DateTime? today = null)
    {
        var effective = (today ?? DateTime.UtcNow).Date.AddYears(-2);
        foreach (var member in claims.Select(c => c.Member).Distinct())
        {
            member.CoverageEffectiveDate = new DateTime(effective.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            member.CoverageTermDate = null;
            member.PlanChangeEffectiveDate = null;
            member.EnrollmentStatus = "Active";
            member.Coverages.Clear();
            member.Dependents.Clear();
            member.Relationship = "Self";
            member.RelationshipCode = "18";
            member.IsSubscriber = true;
            member.SubscriberId = member.MemberId;
            member.MedicaidSpendDownLiabilityAmount = null;
        }
    }

    // A file may also be gzipped (carrier.csv.gz), as the GKE workflow ships them.
    private static string? FindFile(string directory, string name) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(f), name + ".gz", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

    private static StreamReader OpenText(string path) =>
        path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
            : new StreamReader(path);

    private sealed record Beneficiary(DateTime? BirthDate, string? Sex);

    private static Dictionary<string, Beneficiary> LoadBeneficiaries(string directory, TextWriter log)
    {
        var result = new Dictionary<string, Beneficiary>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(directory, "beneficiary_*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csv.gz", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal) // later years overwrite earlier ones
            .ToList();
        foreach (var file in files)
        {
            using var reader = OpenText(file);
            var header = Header(reader.ReadLine(), file);
            var bene = header.Index("BENE_ID");
            var birth = header.Optional("BENE_BIRTH_DT");
            var sex = header.Optional("BENE_SEX_IDENT_CD", "SEX_IDENT_CD");
            if (bene < 0) continue;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var f = line.Split('|');
                var id = Field(f, bene);
                if (string.IsNullOrEmpty(id)) continue;
                result[id] = new Beneficiary(ParseDate(Field(f, birth)), Field(f, sex));
            }
        }

        log.WriteLine($"CMS corpus: {result.Count:N0} beneficiaries from {files.Count} beneficiary files");
        return result;
    }

    private static string Prefix(string kind) =>
        kind switch { "Professional" => "CMS-P-", "Inpatient" => "CMS-I-", _ => "CMS-O-" };

    /// <summary>
    /// First pass: the CLM_IDs that have at least one billable line, in file
    /// order, plus the row count and the claims with none.
    /// </summary>
    private static (List<string> Billable, int Rows, int Skipped) ScanClaimIds(string path, string kind)
    {
        using var reader = OpenText(path);
        var header = Header(reader.ReadLine(), path);
        var professional = kind == "Professional";
        var claimId = header.Index("CLM_ID");
        var beneId = header.Index("BENE_ID");
        var (hcpcs, revenue) = LineCodeColumns(header, path);

        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        var order = new List<string>();
        var rows = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            rows++;
            var f = line.Split('|');
            var id = Field(f, claimId);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(Field(f, beneId))) continue;
            if (!seen.TryGetValue(id, out var billable))
            {
                order.Add(id);
                billable = false;
            }

            seen[id] = billable || IsBillable(professional, Field(f, hcpcs), Field(f, revenue));
        }

        var result = order.Where(id => seen[id]).ToList();
        return (result, rows, order.Count - result.Count);
    }

    private static bool IsBillable(bool professional, string? code, string? revenue) =>
        professional ? !string.IsNullOrEmpty(code) : !string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(revenue);

    private static (int Hcpcs, int Revenue) LineCodeColumns(HeaderMap header, string path)
    {
        var hcpcs = header.Optional("HCPCS_CD", "LINE_HCPCS_CD");
        var revenue = header.Optional("REV_CNTR");
        if (hcpcs < 0 && revenue < 0)
        {
            throw new InvalidOperationException(
                $"CMS synthetic corpus: {Path.GetFileName(path)} has neither HCPCS_CD nor REV_CNTR, so its lines cannot be read. " +
                $"Header: {string.Join('|', header.Names)}");
        }

        return (hcpcs, revenue);
    }

    /// <summary>Second pass: builds the wanted claims only.</summary>
    private static List<SyntheticClaim> ReadClaims(
        string path,
        string kind,
        HashSet<string> wanted,
        InMemoryReferenceDataProvider refData,
        Dictionary<string, SyntheticMember> members,
        Dictionary<string, SyntheticProvider> providers,
        Dictionary<string, Beneficiary> beneficiaries)
    {
        using var reader = OpenText(path);
        var header = Header(reader.ReadLine(), path);
        var professional = kind == "Professional";

        var claimId = header.Index("CLM_ID");
        var beneId = header.Index("BENE_ID");
        var from = header.Index("CLM_FROM_DT");
        var thru = header.Optional("CLM_THRU_DT");
        var (hcpcs, revenue) = LineCodeColumns(header, path);
        var mod1 = header.Optional("HCPCS_1ST_MDFR_CD");
        var mod2 = header.Optional("HCPCS_2ND_MDFR_CD");
        var units = header.Optional(professional ? "LINE_SRVC_CNT" : "REV_CNTR_UNIT_CNT", "LINE_SRVC_CNT", "REV_CNTR_UNIT_CNT");
        var charge = header.Optional(professional ? "LINE_SBMTD_CHRG_AMT" : "REV_CNTR_TOT_CHRG_AMT", "LINE_SBMTD_CHRG_AMT", "REV_CNTR_TOT_CHRG_AMT");
        var lineDate = header.Optional("LINE_1ST_EXPNS_DT", "REV_CNTR_DT");
        var pos = header.Optional("LINE_PLACE_OF_SRVC_CD");
        var principal = header.Optional("PRNCPAL_DGNS_CD", "ICD_DGNS_CD1");
        var lineDiagnosis = header.Optional("LINE_ICD_DGNS_CD");
        var diagnoses = Enumerable.Range(1, 25).Select(i => header.Optional($"ICD_DGNS_CD{i}")).Where(i => i >= 0).ToArray();
        var billingNpi = header.Optional(professional ? "CARR_CLM_BLG_NPI_NUM" : "ORG_NPI_NUM", "CARR_CLM_BLG_NPI_NUM", "ORG_NPI_NUM", "PRVDR_NUM");
        var renderingNpi = header.Optional(professional ? "PRF_PHYSN_NPI" : "AT_PHYSN_NPI", "PRF_PHYSN_NPI", "AT_PHYSN_NPI", "RNDRNG_PHYSN_NPI");
        var drg = header.Optional("CLM_DRG_CD");
        var freq = header.Optional("CLM_FREQ_CD");

        var claims = new Dictionary<string, SyntheticClaim>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split('|');
            var id = Field(f, claimId);
            var bene = Field(f, beneId);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(bene) || !wanted.Contains(id)) continue;

            if (!claims.TryGetValue(id, out var claim))
            {
                var serviceDate = ParseDate(Field(f, from)) ?? new DateTime(2022, 1, 1);
                var serviceEnd = ParseDate(Field(f, thru)) ?? serviceDate;
                var member = MemberFor(bene, refData, members, beneficiaries);
                var institutionalKind = kind == "Inpatient" ? "inpatient" : "outpatient";
                claim = new SyntheticClaim
                {
                    ClaimId = Prefix(kind) + id,
                    ClaimType = professional ? "Professional" : "Institutional",
                    DateOfService = serviceDate,
                    DateReceived = serviceEnd.AddDays(7),
                    Member = member,
                    RenderingProvider = ProviderFor("R:" + (Field(f, renderingNpi) ?? id), refData, providers),
                    BillingProvider = ProviderFor("B:" + (Field(f, billingNpi) ?? id), refData, providers),
                    PlaceOfService = professional
                        ? Field(f, pos) is { Length: > 0 } p ? p.PadLeft(2, '0') : "11"
                        : refData.GetPlaceOfServiceCode(new Random(0), institutionalKind),
                    BenefitPlanId = member.PlanId,
                    PrimaryDiagnosisCode = Icd10(Field(f, principal)) ?? "Z00.00",
                    SecondaryDiagnosisCodes = diagnoses
                        .Select(i => Icd10(Field(f, i)))
                        .Where(c => c is not null)
                        .Select(c => c!)
                        .Distinct(StringComparer.Ordinal)
                        .Take(11)
                        .ToList(),
                    FrequencyCode = professional ? null : Field(f, freq) is { Length: > 0 } fc ? fc : "1",
                    BillType = professional ? null : kind == "Inpatient" ? "0111" : "0131",
                    DrgCode = kind == "Inpatient" ? Field(f, drg) : null,
                    PriorAuthStatus = "NotRequired",
                    FhirResourceGenerated = true,
                    PayerToPayerReady = true,
                    ExpectedOutcome = new ExpectedOutcome { Disposition = "Unspecified" },
                };
                claim.SecondaryDiagnosisCodes.Remove(claim.PrimaryDiagnosisCode);
                claims[id] = claim;
            }

            var code = Field(f, hcpcs);
            var rev = Field(f, revenue);
            if (!IsBillable(professional, code, rev)) continue;

            var lineServiceDate = ParseDate(Field(f, lineDate)) ?? claim.DateOfService;
            var claimLine = new ClaimLine
            {
                LineNumber = claim.Lines.Count + 1,
                ProcedureCode = code ?? string.Empty,
                Description = string.Empty,
                Modifiers = new[] { Field(f, mod1), Field(f, mod2) }
                    .Where(m => !string.IsNullOrEmpty(m))
                    .Select(m => m!)
                    .ToList(),
                RevenueCode = professional ? null : rev?.PadLeft(4, '0'),
                DiagnosisPointers = new List<int> { 1 },
                Units = Math.Max(1m, ParseDecimal(Field(f, units)) ?? 1m),
                ChargeAmount = Math.Max(0m, ParseDecimal(Field(f, charge)) ?? 0m),
                ServiceDate = lineServiceDate,
                ServiceEndDate = professional ? null : ParseDate(Field(f, thru)),
                PlaceOfService = professional ? claim.PlaceOfService : null,
            };
            if (professional && Icd10(Field(f, lineDiagnosis)) is { } lineDx
                && lineDx != claim.PrimaryDiagnosisCode && !claim.SecondaryDiagnosisCodes.Contains(lineDx)
                && claim.SecondaryDiagnosisCodes.Count < 11)
            {
                claim.SecondaryDiagnosisCodes.Add(lineDx);
            }

            claim.Lines.Add(claimLine);
        }

        // The first pass only lets through claims with a billable line.
        var usable = claims.Values.ToList();
        foreach (var claim in usable)
        {
            claim.TotalCharges = claim.Lines.Sum(l => l.ChargeAmount);
        }

        return usable;
    }

    private static SyntheticMember MemberFor(
        string beneId,
        InMemoryReferenceDataProvider refData,
        Dictionary<string, SyntheticMember> members,
        Dictionary<string, Beneficiary> beneficiaries)
    {
        if (members.TryGetValue(beneId, out var existing)) return existing;

        var member = refData.GenerateMember(new Random(StableHash("bene:" + beneId)));
        var key = $"CMS{(uint)StableHash("member:" + beneId):D10}";
        member.MemberId = key;
        member.SubscriberId = key;
        if (beneficiaries.TryGetValue(beneId, out var b))
        {
            if (b.BirthDate is { } dob) member.DateOfBirth = dob;
            member.Gender = b.Sex switch { "1" => "M", "2" => "F", _ => member.Gender };
        }

        members[beneId] = member;
        return member;
    }

    private static SyntheticProvider ProviderFor(
        string key,
        InMemoryReferenceDataProvider refData,
        Dictionary<string, SyntheticProvider> providers)
    {
        if (providers.TryGetValue(key, out var existing)) return existing;
        var provider = refData.GenerateProvider(new Random(StableHash("provider:" + key)));
        providers[key] = provider;
        return provider;
    }

    internal static string? Icd10(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var code = raw.Trim().ToUpperInvariant();
        if (code.Contains('.') || code.Length <= 3) return code;
        return code[..3] + "." + code[3..];
    }

    internal static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "dd-MMM-yyyy", "d-MMM-yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd HH:mm:ss"];
        return DateTime.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d.Date, DateTimeKind.Utc)
            : null;
    }

    private static decimal? ParseDecimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Field(string[] fields, int index) =>
        index >= 0 && index < fields.Length && fields[index].Length > 0 ? fields[index].Trim() : null;

    internal static int StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
    }

    private sealed class HeaderMap(string[] names)
    {
        public string[] Names { get; } = names;

        private readonly Dictionary<string, int> _index = names
            .Select((n, i) => (Name: n.Trim().Trim('"').ToUpperInvariant(), i))
            .GroupBy(x => x.Name)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);

        public int Index(string name) =>
            _index.TryGetValue(name, out var i)
                ? i
                : throw new InvalidOperationException(
                    $"CMS synthetic corpus: required column {name} is missing. Header: {string.Join('|', Names)}");

        public int Optional(params string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (_index.TryGetValue(c, out var i)) return i;
            }

            return -1;
        }
    }

    private static HeaderMap Header(string? line, string path) =>
        string.IsNullOrWhiteSpace(line)
            ? throw new InvalidOperationException($"CMS synthetic corpus: {path} is empty")
            : new HeaderMap(line.Split('|'));
}
