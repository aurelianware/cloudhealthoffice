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
/// SBR03, the insured group or policy number in loop 2000B
/// (plan-configuration.md §12.14).
///
/// <para>
/// It was written empty, which the IG permits for a situational element but
/// which tells the receiver nothing about the group the encounter falls under.
/// The group number arrives on the create request rather than from a coverage
/// lookup: the caller builds an encounter from an adjudicated claim that
/// already resolved the member's coverage, so resolving it again here would be
/// a second outbound dependency on the submission path for a fact the caller
/// already holds.
/// </para>
/// </summary>
public class Encounter837Sbr03Tests
{
    private const string Tenant = "t1";

    private static readonly Encounter837Config Config = new();

    private static Encounter Encounter(string? groupNumber) => new()
    {
        Id = "enc-1",
        TenantId = Tenant,
        EncounterControlNumber = "ENC-1",
        ClaimId = "CLM-1",
        MemberId = "M1",
        SubscriberId = "SUB001",
        GroupNumber = groupNumber,
        SubscriberFirstName = "Jane",
        SubscriberLastName = "Doe",
        BillingProviderNPI = "1234567890",
        BillingProviderName = "ACME MEDICAL GROUP",
        PayerId = "PAYER1",
        PayerName = "State Medicaid",
        LineOfBusiness = LineOfBusiness.Medicaid,
        Status = EncounterStatus.Pending,
        PlaceOfServiceCode = "11",
        ClaimFrequencyCode = "1",
        ServiceDateFrom = new DateTime(2026, 1, 15),
        ServiceDateTo = new DateTime(2026, 1, 15),
        ServiceLines =
        {
            new EncounterServiceLine
            {
                LineNumber = 1, ProcedureCode = "99213", Units = 1, ChargeAmount = 200m,
                ServiceDateFrom = new DateTime(2026, 1, 15),
            },
        },
    };

    private static string Sbr(string? groupNumber) =>
        new Encounter837Service(NullLogger<Encounter837Service>.Instance)
            .Generate837(Encounter(groupNumber), Config)
            .Split('~', StringSplitOptions.RemoveEmptyEntries)
            .First(s => s.TrimStart().StartsWith("SBR*", StringComparison.Ordinal))
            .TrimStart();

    private static string Element(string segment, int position) => segment.Split('*')[position];

    [Fact]
    public void The_group_number_is_what_SBR03_carries()
    {
        Assert.Equal("GRP0001", Element(Sbr("GRP0001"), 3));
    }

    [Fact]
    public void An_encounter_with_no_group_number_writes_the_segment_exactly_as_before()
    {
        // The whole-segment assertion on purpose: every encounter on file today
        // has no group number, so this is the output that must not move.
        Assert.Equal("SBR*P*18*****MC", Sbr(null));
    }

    [Fact]
    public void The_group_number_does_not_displace_any_other_element()
    {
        // A receiver reads the segment by position, so an element that shifts is
        // a silently wrong file rather than a rejected one.
        var withGroup = Sbr("GRP0001").Split('*');
        var without = Sbr(null).Split('*');

        Assert.Equal(without.Length, withGroup.Length);
        Assert.Equal("P", withGroup[1]);
        Assert.Equal("18", withGroup[2]);
        Assert.Equal("MC", withGroup[^1]);
    }

    [Fact]
    public void The_subscriber_id_is_not_the_group_number_and_stays_out_of_SBR03()
    {
        // The two sit one element apart in meaning and would be easy to conflate:
        // SBR03 is a group, NM1*IL carries the person.
        var sbr = Sbr("GRP0001");

        Assert.DoesNotContain("SUB001", sbr);
        Assert.Contains("SUB001", new Encounter837Service(NullLogger<Encounter837Service>.Instance)
            .Generate837(Encounter("GRP0001"), Config));
    }

    [Fact]
    public void A_group_number_carrying_a_delimiter_cannot_split_the_segment()
    {
        // Reference data is not always clean, and a stray asterisk or tilde
        // would turn one segment into two, shifting everything after it.
        var sbr = Sbr("GRP*0001~X");

        Assert.Equal(Sbr(null).Split('*').Length, sbr.Split('*').Length);
        Assert.Equal("GRP 0001 X", Element(sbr, 3));
    }
}

/// <summary>
/// A correction or a resubmission is built from the original encounter, so the
/// group number has to come across with it — otherwise the replacement file
/// says less about the member than the file it replaces.
/// </summary>
public class EncounterGroupNumberCarriesForwardTests
{
    private const string Tenant = "t1";

    private readonly Mock<IEncounterRepository> _repo = new();
    private readonly List<Encounter> _created = new();

    private EncountersController Build(Encounter original)
    {
        _repo.Setup(r => r.GetByIdAsync(original.Id)).ReturnsAsync(original);
        _repo.Setup(r => r.CreateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) => { _created.Add(e); return e; });
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Encounter>())).ReturnsAsync((Encounter e) => e);

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

    private static Encounter Original(EncounterStatus status) => new()
    {
        Id = "enc-1",
        TenantId = Tenant,
        EncounterControlNumber = "ENC-1",
        ClaimId = "CLM-1",
        MemberId = "M1",
        SubscriberId = "SUB001",
        GroupNumber = "GRP0001",
        BillingProviderNPI = "1234567890",
        PayerId = "PAYER1",
        LineOfBusiness = LineOfBusiness.Medicaid,
        Status = status,
        ServiceDateFrom = new DateTime(2026, 1, 15),
        ServiceDateTo = new DateTime(2026, 1, 15),
        ServiceLines =
        {
            new EncounterServiceLine
            {
                LineNumber = 1, ProcedureCode = "99213", Units = 1, ChargeAmount = 200m,
                ServiceDateFrom = new DateTime(2026, 1, 15),
            },
        },
    };

    [Fact]
    public async Task A_resubmission_keeps_the_group_number()
    {
        var ctl = Build(Original(EncounterStatus.Rejected));

        await ctl.ResubmitEncounter("enc-1");

        Assert.NotEmpty(_created);
        Assert.All(_created, e => Assert.Equal("GRP0001", e.GroupNumber));
    }

    [Fact]
    public async Task A_correction_keeps_it_on_both_the_void_and_the_replacement()
    {
        // The void is built from the original, so it inherits. The replacement
        // is the caller's own body, and a caller that omits the group number --
        // including any written before the field existed -- must not end up
        // sending a replacement that says less than the file it replaces.
        var ctl = Build(Original(EncounterStatus.Accepted));
        var corrected = Original(EncounterStatus.Pending);
        corrected.GroupNumber = null;

        await ctl.SubmitCorrection("enc-1", new CorrectionRequest
        {
            CorrectedEncounter = corrected,
            CorrectionReason = "Wrong procedure code",
        });

        Assert.Equal(2, _created.Count);
        Assert.All(_created, e => Assert.Equal("GRP0001", e.GroupNumber));
    }

    [Fact]
    public async Task A_correction_that_names_a_different_group_is_taken_at_its_word()
    {
        // The inheritance fills a gap; it does not overrule the caller.
        var ctl = Build(Original(EncounterStatus.Accepted));
        var corrected = Original(EncounterStatus.Pending);
        corrected.GroupNumber = "GRP0002";

        await ctl.SubmitCorrection("enc-1", new CorrectionRequest
        {
            CorrectedEncounter = corrected,
            CorrectionReason = "Filed under the wrong group",
        });

        Assert.Equal("GRP0002", _created.Single(e => e.SubmissionType == SubmissionType.Correction).GroupNumber);
    }
}
