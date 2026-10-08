using System.Globalization;
using CloudHealthOffice.EncounterService.Tests.Support;
using EncounterService.Controllers;
using EncounterService.Models;
using EncounterService.Repositories;
using EncounterService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.EncounterService.Tests.Domain;

/// <summary>
/// EncounterServiceLine.ChargeAmount is the line TOTAL (837 SV102/SV203),
/// not a per-unit price. The encounter total (CLM02) must therefore be the
/// plain sum of line charges so CLM02 = Σ SV102 balances on multi-unit lines.
/// </summary>
public class EncounterLineChargeSemanticsTests
{
    private const string Tenant = "t1";

    private readonly Mock<IEncounterRepository> _repo = new();
    private readonly List<Encounter> _created = new();

    private EncountersController Build()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) => { _created.Add(e); return e; });
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) => e);
        var ctl = new EncountersController(
            _repo.Object,
            Mock.Of<IEncounter837Service>(),
            new ConfigurationBuilder().Build(),
            new TestActor("user-1", Tenant),
            NullLogger<EncountersController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctl;
    }

    private static Encounter MultiUnitEncounter() => new()
    {
        Id = "enc-1", TenantId = Tenant, EncounterControlNumber = "ENC-1", ClaimId = "CLM-1",
        MemberId = "M1", BillingProviderNPI = "1234567890", PayerId = "PAYER1",
        LineOfBusiness = LineOfBusiness.Medicaid, Status = EncounterStatus.Rejected,
        ServiceDateFrom = new DateTime(2026, 1, 15), ServiceDateTo = new DateTime(2026, 1, 15),
        ServiceLines =
        {
            // 4 units billed for a line total of $200 (i.e. $50/unit).
            new EncounterServiceLine
            {
                LineNumber = 1, ProcedureCode = "97110", Units = 4, ChargeAmount = 200m,
                ServiceDateFrom = new DateTime(2026, 1, 15), ServiceDateTo = new DateTime(2026, 1, 15),
            },
            new EncounterServiceLine
            {
                LineNumber = 2, ProcedureCode = "99213", Units = 1, ChargeAmount = 125m,
                ServiceDateFrom = new DateTime(2026, 1, 15), ServiceDateTo = new DateTime(2026, 1, 15),
            },
        }
    };

    [Fact]
    public async Task SubmitEncounter_TotalCharge_IsSumOfLineTotals_NotTimesUnits()
    {
        var ctl = Build();

        await ctl.SubmitEncounter(MultiUnitEncounter());

        _created.Should().ContainSingle();
        _created[0].TotalChargeAmount.Should().Be(325m);
    }

    [Fact]
    public async Task SubmitCorrection_ReplacementTotalCharge_IsSumOfLineTotals()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync("enc-1")).ReturnsAsync(MultiUnitEncounter());

        await ctl.SubmitCorrection("enc-1", new CorrectionRequest
        {
            CorrectedEncounter = MultiUnitEncounter(),
            CorrectionReason = "units"
        });

        _created.Should().Contain(e => e.SubmissionType == SubmissionType.Correction);
        _created.Single(e => e.SubmissionType == SubmissionType.Correction)
            .TotalChargeAmount.Should().Be(325m);
    }

    [Fact]
    public async Task Generated837_Clm02_EqualsSumOfSv102()
    {
        var ctl = Build();
        await ctl.SubmitEncounter(MultiUnitEncounter());
        var svc = new Encounter837Service(NullLogger<Encounter837Service>.Instance);

        var edi = svc.Generate837(_created[0], new Encounter837Config());

        var segments = edi.Split('~', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var clm02 = decimal.Parse(
            segments.Single(s => s.StartsWith("CLM*")).Split('*')[2], CultureInfo.InvariantCulture);
        var sv102Sum = segments.Where(s => s.StartsWith("SV1*"))
            .Sum(s => decimal.Parse(s.Split('*')[2], CultureInfo.InvariantCulture));

        clm02.Should().Be(325m);
        sv102Sum.Should().Be(clm02);
    }
}
