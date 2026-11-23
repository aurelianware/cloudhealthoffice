using System.Text;
using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services.CodeSets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.TerminologyService.Tests;

/// <summary>
/// Dated code sets without Mongo: the parsers, release windows and the
/// valid / invalid / unknown answers. The Mongo repository and the HTTP surface
/// are covered by <see cref="CodeSetPipelineTests"/>.
/// </summary>
public sealed class CodeSetValidationServiceTests
{
    private const string Cm = CodeSetSystems.Icd10Cm;
    private const string Hcpcs = CodeSetSystems.Hcpcs;

    private readonly InMemoryCodeSetRepository _repository = new();
    private readonly CodeSetValidationService _service;

    public CodeSetValidationServiceTests()
        => _service = new CodeSetValidationService(_repository, NullLogger<CodeSetValidationService>.Instance);

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>One line of a CMS order file in its fixed-width layout.</summary>
    internal static string OrderLine(int order, string code, bool billable, string shortDescription, string longDescription)
        => $"{order:D5} {code,-7} {(billable ? '1' : '0')} {shortDescription,-60} {longDescription}";

    internal static Stream OrderFile(params string[] lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    internal static string Fy2026Order() => string.Join("\n",
        OrderLine(1, "E11", false, "Type 2 diabetes mellitus", "Type 2 diabetes mellitus"),
        OrderLine(2, "E1165", true, "Type 2 diabetes mellitus with hyperglycemia", "Type 2 diabetes mellitus with hyperglycemia"),
        OrderLine(3, "Z23", true, "Encounter for immunization", "Encounter for immunization"));

    private Task<CodeSetLoadResult> LoadCm(string version, string file, DateOnly? from = null, DateOnly? to = null)
        => _service.LoadAsync(new CodeSetLoadRequest
        {
            System = Cm, Format = Icd10OrderFileParser.Format, Version = version, EffectiveFrom = from, EffectiveTo = to,
        }, new MemoryStream(Encoding.UTF8.GetBytes(file)));

    private Task<CodeSetLoadResult> LoadHcpcs(string csv, DateOnly from, string version = "2026-Q1")
        => _service.LoadAsync(new CodeSetLoadRequest
        {
            System = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets", Format = HcpcsCsvParser.Format,
            Version = version, EffectiveFrom = from,
        }, new MemoryStream(Encoding.UTF8.GetBytes(csv)));

    private Task<CodeValidityResponse> Check(string system, string code, string date)
        => _service.ValidateAsync(new CodeValidityRequest { System = system, Code = code, Date = DateOnly.Parse(date) });

    // ── Parsers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OrderFile_ReadsCodesHeadersAndLongDescriptions()
    {
        var parsed = await Icd10OrderFileParser.ParseAsync(OrderFile(Fy2026Order(), "garbage line that is not an order file row"));

        Assert.Equal(3, parsed.Codes.Count);
        Assert.Equal(1, parsed.RowsSkipped);
        var header = parsed.Codes[0];
        Assert.Equal("E11", header.Code);
        Assert.False(header.Billable);
        Assert.Equal("E1165", parsed.Codes[1].Code);
        Assert.True(parsed.Codes[1].Billable);
        Assert.Equal("Type 2 diabetes mellitus with hyperglycemia", parsed.Codes[1].Display);
    }

    [Fact]
    public async Task HcpcsCsv_CmsLayout_JoinsContinuationRowsAndReadsDates()
    {
        var csv = "HCPC,SEQNUM,RECID,LONG DESCRIPTION,SHORT DESCRIPTION,ADD DT,ACT EFF DT,TERM DT\n" +
                  "G0008,00100,3,\"Administration of influenza virus vaccine, first part\",Admin influenza virus vac,19930101,20260101,\n" +
                  "G0008,00200,4,of the description,,,,\n" +
                  "J9999,00100,3,Old drug,Old drug,20100101,20100101,20251231\n";

        var parsed = await HcpcsCsvParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Empty(parsed.Errors);
        Assert.Equal(2, parsed.Codes.Count);
        Assert.Equal("Administration of influenza virus vaccine, first part of the description", parsed.Codes[0].Display);
        Assert.Equal(new DateTime(1993, 1, 1), parsed.Codes[0].EffectiveFrom);
        Assert.Null(parsed.Codes[0].EffectiveTo);
        Assert.Equal(new DateTime(2025, 12, 31), parsed.Codes[1].EffectiveTo);
    }

    [Fact]
    public async Task HcpcsCsv_PlainLayout_IsAccepted()
    {
        var csv = "code,display,effective_date,end_date\nA0425,Ground mileage,2000-01-01,\n";

        var parsed = await HcpcsCsvParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)));

        Assert.Equal("A0425", Assert.Single(parsed.Codes).Code);
    }

    // ── Windows ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FY2026", 2026)]
    [InlineData("fy26", 2026)]
    [InlineData("2026", 2026)]
    [InlineData("ICD10CM-FY2026-APR", 2026)]
    [InlineData("April update", null)]
    public void FiscalYear_IsReadFromTheVersion(string version, int? expected)
        => Assert.Equal(expected, CodeSetValidationService.FiscalYear(version));

    [Fact]
    public void Window_Icd10_DefaultsToTheFiscalYear()
    {
        var window = CodeSetValidationService.Window(Cm, "FY2026", null, null)!.Value;
        Assert.Equal(new DateTime(2025, 10, 1), window.From);
        Assert.Equal(new DateTime(2026, 9, 30), window.To);
    }

    [Fact]
    public void Window_Hcpcs_DefaultsToTheQuarter()
    {
        var window = CodeSetValidationService.Window(Hcpcs, "2026-Q2", new DateOnly(2026, 4, 1), null)!.Value;
        Assert.Equal(new DateTime(2026, 6, 30), window.To);
    }

    [Fact]
    public void Window_WithoutAStart_IsRefused()
        => Assert.Null(CodeSetValidationService.Window(Hcpcs, "latest", null, null));

    // ── Answers ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_InvalidAndNotBillable_InsideTheRelease()
    {
        Assert.True((await LoadCm("FY2026", Fy2026Order())).Success);

        var valid = await Check(Cm, "E11.65", "2026-03-01");
        Assert.Equal(CodeValidityStatus.Valid, valid.Status);
        Assert.True(valid.Result);
        Assert.Equal("FY2026", valid.ReleaseVersion);

        var header = await Check(Cm, "E11", "2026-03-01");
        Assert.False(header.Result);
        Assert.Equal(CodeValidityReason.NotBillable, header.Reason);

        var missing = await Check(Cm, "Q99.99", "2026-03-01");
        Assert.False(missing.Result);
        Assert.Equal(CodeValidityReason.NotInCodeSet, missing.Reason);
    }

    [Fact]
    public async Task DateNoReleaseCovers_IsUnknown_NotInvalid()
    {
        await LoadCm("FY2026", Fy2026Order());

        var after = await Check(Cm, "Z23", "2026-10-01");
        var unloaded = await Check(CodeSetSystems.Icd10Pcs, "0DTJ4ZZ", "2026-03-01");
        var cpt = await Check("http://www.ama-assn.org/go/cpt", "99213", "2026-03-01");

        Assert.Null(after.Result);
        Assert.Equal(CodeValidityReason.NoReleaseForDate, after.Reason);
        Assert.Equal(CodeValidityReason.CodeSystemNotLoaded, unloaded.Reason);
        Assert.Equal(CodeValidityStatus.Unknown, cpt.Status);
    }

    [Fact]
    public async Task CodeDeletedInTheNextFiscalYear_IsTerminatedAfterIt()
    {
        await LoadCm("FY2026", Fy2026Order());
        await LoadCm("FY2027", string.Join("\n",
            OrderLine(1, "E1165", true, "T2DM w hyperglycemia", "Type 2 diabetes mellitus with hyperglycemia"),
            OrderLine(2, "U099", true, "New code", "A code new in FY2027")));

        var before = await Check(Cm, "Z23", "2026-09-30");
        var afterDeletion = await Check(Cm, "Z23", "2026-10-01");
        var newCodeTooEarly = await Check(Cm, "U09.9", "2026-09-30");

        Assert.True(before.Result);
        Assert.Equal(CodeValidityReason.Terminated, afterDeletion.Reason);
        Assert.Equal(new DateOnly(2026, 9, 30), afterDeletion.EffectiveTo);
        Assert.Equal(CodeValidityReason.NotYetEffective, newCodeTooEarly.Reason);
        Assert.Equal(new DateOnly(2026, 10, 1), newCodeTooEarly.EffectiveFrom);
    }

    [Fact]
    public async Task AprilUpdate_EndsTheOctoberReleaseTheDayBefore()
    {
        await LoadCm("FY2026", Fy2026Order());
        var april = await LoadCm("FY2026-APR", OrderLine(1, "E1165", true, "x", "Type 2 diabetes mellitus with hyperglycemia"),
            from: new DateOnly(2026, 4, 1));

        Assert.Equal(new DateOnly(2026, 9, 30), april.EffectiveTo);
        Assert.Contains("FY2026: now ends 2026-03-31", april.AdjustedReleases);
        Assert.True((await Check(Cm, "Z23", "2026-03-31")).Result);
        Assert.Equal(CodeValidityReason.Terminated, (await Check(Cm, "Z23", "2026-04-01")).Reason);
    }

    [Fact]
    public async Task ReloadingAVersion_ReplacesIt()
    {
        await LoadCm("FY2026", Fy2026Order());
        var again = await LoadCm("FY2026", OrderLine(1, "Z23", true, "x", "Encounter for immunization"));

        Assert.Contains("FY2026: replaced", again.AdjustedReleases);
        Assert.Single(await _repository.GetReleasesAsync(Cm));
        Assert.Equal(CodeValidityReason.NotInCodeSet, (await Check(Cm, "E11.65", "2026-03-01")).Reason);
    }

    [Fact]
    public async Task Hcpcs_TermDateInsideTheQuarter_EndsTheCode()
    {
        var csv = "HCPC,RECID,LONG DESCRIPTION,ADD DT,TERM DT\n" +
                  "J9999,3,Old drug,20100101,20260215\n" +
                  "J1234,3,New drug,20260301,\n";
        Assert.True((await LoadHcpcs(csv, new DateOnly(2026, 1, 1))).Success);

        Assert.True((await Check(Hcpcs, "J9999", "2026-02-15")).Result);
        Assert.Equal(CodeValidityReason.Terminated, (await Check(Hcpcs, "j9999", "2026-02-16")).Reason);
        Assert.Equal(CodeValidityReason.NotYetEffective, (await Check(Hcpcs, "J1234", "2026-02-28")).Reason);
        Assert.True((await Check(Hcpcs, "J1234", "2026-03-31")).Result);
        Assert.Equal(CodeValidityStatus.Unknown, (await Check(Hcpcs, "J1234", "2026-04-01")).Status);
    }

    [Fact]
    public async Task Batch_AnswersInRequestOrder_AcrossSystems()
    {
        await LoadCm("FY2026", Fy2026Order());

        var answers = await _service.ValidateManyAsync(
        [
            new CodeValidityRequest { System = Hcpcs, Code = "A0425", Date = new DateOnly(2026, 3, 1) },
            new CodeValidityRequest { System = Cm, Code = "Z23", Date = new DateOnly(2026, 3, 1) },
            new CodeValidityRequest { System = Cm, Code = "E11", Date = new DateOnly(2026, 3, 1) },
        ]);

        Assert.Equal([CodeValidityStatus.Unknown, CodeValidityStatus.Valid, CodeValidityStatus.Invalid],
            answers.Select(a => a.Status).ToArray());
    }

    [Theory]
    [InlineData("HCPCS-CSV", Cm)]
    [InlineData("ICD10-ORDER", Hcpcs)]
    [InlineData("RF2", Cm)]
    public async Task Load_RefusesAFormatThatDoesNotFitTheSystem(string format, string system)
    {
        var result = await _service.LoadAsync(new CodeSetLoadRequest
        {
            System = system, Format = format, Version = "FY2026", EffectiveFrom = new DateOnly(2026, 1, 1),
        }, new MemoryStream(Encoding.UTF8.GetBytes(Fy2026Order())));

        Assert.False(result.Success);
        Assert.Empty(await _repository.GetReleasesAsync());
    }
}

/// <summary>A list-backed <see cref="ICodeSetRepository"/> with the Mongo repository's semantics.</summary>
internal sealed class InMemoryCodeSetRepository : ICodeSetRepository
{
    private readonly List<CodeSetRelease> _releases = [];
    private readonly List<CodeValidityPeriod> _periods = [];

    public Task<List<CodeSetRelease>> GetReleasesAsync(string? system = null, CancellationToken ct = default)
        => Task.FromResult(_releases.Where(r => system is null || r.System == system).OrderBy(r => r.EffectiveFrom).ToList());

    public Task<List<CodeValidityPeriod>> FindPeriodsAsync(string system, string code, CancellationToken ct = default)
        => Task.FromResult(_periods.Where(p => p.System == system && p.Code == code).ToList());

    public Task<List<CodeValidityPeriod>> FindPeriodsAsync(string system, IReadOnlyCollection<string> codes, CancellationToken ct = default)
        => Task.FromResult(_periods.Where(p => p.System == system && codes.Contains(p.Code)).ToList());

    public Task SaveReleaseAsync(CodeSetRelease release, IReadOnlyList<CodeValidityPeriod> periods, CancellationToken ct = default)
    {
        _periods.AddRange(periods);
        _releases.RemoveAll(r => r.Id == release.Id);
        _releases.Add(release);
        return Task.CompletedTask;
    }

    public Task EndReleaseAsync(string releaseId, DateTime effectiveTo, CancellationToken ct = default)
    {
        foreach (var r in _releases.Where(r => r.Id == releaseId)) r.EffectiveTo = effectiveTo;
        foreach (var p in _periods.Where(p => p.ReleaseId == releaseId && (p.EffectiveTo is null || p.EffectiveTo > effectiveTo)))
            p.EffectiveTo = effectiveTo;
        _periods.RemoveAll(p => p.ReleaseId == releaseId && p.EffectiveFrom > effectiveTo);
        return Task.CompletedTask;
    }

    public Task DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        _releases.RemoveAll(r => r.Id == releaseId);
        _periods.RemoveAll(p => p.ReleaseId == releaseId);
        return Task.CompletedTask;
    }
}
