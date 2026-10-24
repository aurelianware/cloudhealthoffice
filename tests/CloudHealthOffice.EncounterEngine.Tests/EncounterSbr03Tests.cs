using CloudHealthOffice.EncounterEngine.Domain;
using CloudHealthOffice.EncounterEngine.Services;
using Xunit;

namespace CloudHealthOffice.EncounterEngine.Tests;

/// <summary>
/// SBR03, the insured group or policy number in loop 2000B
/// (plan-configuration.md §12.14).
///
/// <para>
/// It carried the subscriber id — a person identifier in an element that means
/// a group — so the group number never went at all. The subscriber's own
/// identifier belongs in the NM1*IL of the same loop, and since SBR02 is 18
/// (the patient is the subscriber) the member id there already is it. These pin
/// the group number in SBR03, the subscriber identity still travelling in
/// NM1*IL, and every other element of the segment keeping its position, since a
/// receiver reads the segment positionally and a shifted element is a silently
/// wrong file rather than a rejected one.
/// </para>
/// </summary>
public class EncounterSbr03Tests
{
    private static EncounterInput Input(string? groupNumber) => new()
    {
        ClaimId = "CLM001",
        TenantId = "TENANT1",
        FormType = ClaimFormType.Professional,
        FrequencyCode = ClaimFrequencyCode.Original,
        ServiceDate = new DateOnly(2026, 1, 15),
        PlaceOfService = "11",
        MemberId = "MEM001",
        SubscriberId = "SUB001",
        GroupNumber = groupNumber,
        MemberFirstName = "Jane",
        MemberLastName = "Doe",
        MemberDateOfBirth = new DateOnly(1980, 6, 15),
        MemberGender = "F",
        BillingNpi = "1234567890",
        BillingProviderName = "ACME MEDICAL GROUP",
        BillingTaxId = "123456789",
        PlanSubmitterId = "PLAN001",
        ReceiverSubmitterId = "CMS001",
        PlanName = "Test Health Plan",
        PlanPayerId = "88888",
        DiagnosisCodes = ["Z00.00"],
        Lines =
        [
            new EncounterLineInput
            {
                LineNumber = 1, ProcedureCode = "99213", BilledAmount = 200m, AllowedAmount = 160m,
                PlanPaidAmount = 128m, MemberResponsibility = 32m, Units = 1, DiagnosisPointers = ["1"],
            }
        ],
    };

    private static string Segment(string? groupNumber, string prefix) =>
        new EncounterTransformer().Transform(Input(groupNumber)).RawX12
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(s => s.StartsWith(prefix + "*", StringComparison.Ordinal));

    private static string Element(string segment, int position) =>
        segment.Split('*')[position];

    [Fact]
    public void The_group_number_is_what_SBR03_carries()
    {
        var sbr = Segment("GRP0001", "SBR");

        Assert.Equal("GRP0001", Element(sbr, 3));
    }

    [Fact]
    public void The_subscriber_id_is_no_longer_in_SBR03()
    {
        // The defect this closes. It is not merely redundant: a receiver reading
        // SBR03 as the group number was told the member's own id instead.
        var sbr = Segment("GRP0001", "SBR");

        Assert.DoesNotContain("SUB001", sbr);
    }

    [Fact]
    public void The_subscriber_identity_still_travels_in_the_loop_that_carries_it()
    {
        // SBR02 is 18, so the patient is the subscriber and the member id in
        // NM1*IL is the subscriber's identifier. Taking the subscriber id out
        // of SBR03 therefore loses nothing a receiver was reading.
        var nm1 = Segment("GRP0001", "NM1*IL");

        Assert.Contains("MEM001", nm1);
        Assert.Equal("18", Element(Segment("GRP0001", "SBR"), 2));
    }

    [Fact]
    public void An_encounter_with_no_group_number_writes_SBR03_empty()
    {
        // Situational in the IG, so an empty element is conformant. Writing
        // nothing beats inventing a group number.
        var sbr = Segment(null, "SBR");

        Assert.Equal(string.Empty, Element(sbr, 3));
    }

    [Theory]
    [InlineData("GRP0001")]
    [InlineData(null)]
    public void Every_other_element_of_the_segment_keeps_its_position(string? groupNumber)
    {
        // A receiver reads the segment by position, so an element that shifts is
        // a silently wrong file rather than a rejected one. SBR01, SBR02 and
        // the two codes after SBR03 must sit exactly where they sat before.
        var parts = Segment(groupNumber, "SBR").Split('*');

        Assert.Equal(9, parts.Length);
        Assert.Equal("P", parts[1]);
        Assert.Equal("18", parts[2]);
        Assert.Equal("HM", parts[6]);
        Assert.Equal("MB", parts[8]);
    }

    [Fact]
    public void A_group_number_carrying_a_delimiter_cannot_split_the_segment()
    {
        // Reference data is not always clean, and a stray asterisk would turn
        // one segment into two, shifting every element after it.
        var sbr = Segment("GRP*0001~X", "SBR");

        Assert.Equal(9, sbr.Split('*').Length);
        Assert.Equal("GRP0001X", Element(sbr, 3));
    }
}
