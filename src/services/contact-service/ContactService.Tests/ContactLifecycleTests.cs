using ContactService.Models;
using ContactService.Services;

namespace ContactService.Tests;

/// <summary>The rules a contact obeys, on their own.</summary>
public class ContactLifecycleTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_open_contact_is_editable()
    {
        ContactLifecycle.WhyNotEditable(new Contact { Status = ContactStatus.Open })
            .Should().BeNull();
    }

    [Fact]
    public void A_closed_contact_is_not()
    {
        ContactLifecycle.WhyNotEditable(new Contact { Status = ContactStatus.Closed })
            .Should().NotBeNull();
    }

    [Fact]
    public void A_start_time_slightly_ahead_of_the_clock_is_allowed()
    {
        // Clock skew between CHO and a phone platform handing over a call.
        ContactLifecycle.WhyNotStartable(Now.AddMinutes(2), Now).Should().BeNull();
    }

    [Fact]
    public void A_start_time_well_ahead_of_the_clock_is_not()
    {
        ContactLifecycle.WhyNotStartable(Now.AddHours(1), Now).Should().NotBeNull();
    }

    [Fact]
    public void A_contact_recorded_after_the_fact_is_fine()
    {
        ContactLifecycle.WhyNotStartable(Now.AddDays(-3), Now).Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(3600, 3600)]
    public void Duration_is_the_talk_time_in_seconds(int seconds, int expected)
    {
        ContactLifecycle.Duration(Now, Now.AddSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void Duration_is_null_while_the_contact_is_open()
    {
        ContactLifecycle.Duration(Now, null).Should().BeNull();
    }

    [Fact]
    public void Duration_never_goes_negative()
    {
        // Close already refuses this, so if it ever arrives the reporting
        // figure should still be sane rather than poisoning an average.
        ContactLifecycle.Duration(Now, Now.AddSeconds(-30)).Should().Be(0);
    }

    [Fact]
    public void Reason_codes_are_upper_cased_and_de_duplicated()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BENEFITS", "ID_CARD" };

        var (accepted, unknown) = ContactLifecycle.SiftReasonCodes(
            [" benefits ", "BENEFITS", "id_card"], known);

        accepted.Should().Equal("BENEFITS", "ID_CARD");
        unknown.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_reason_code_is_reported_rather_than_kept()
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BENEFITS" };

        var (accepted, unknown) = ContactLifecycle.SiftReasonCodes(
            ["BENEFITS", "RETIRED_LAST_WEEK", ""], known);

        accepted.Should().Equal("BENEFITS");
        unknown.Should().Equal("RETIRED_LAST_WEEK");
    }

    [Fact]
    public void A_contact_number_carries_the_day_it_started()
    {
        ContactLifecycle.NextContactNumber(Now).Should().MatchRegex("^C-20261022-[0-9A-F]{8}$");
    }

    [Fact]
    public void Two_contact_numbers_in_the_same_second_differ()
    {
        ContactLifecycle.NextContactNumber(Now)
            .Should().NotBe(ContactLifecycle.NextContactNumber(Now));
    }

    [Fact]
    public void Recording_an_action_stamps_the_contact_as_well_as_the_history()
    {
        var contact = new Contact();

        ContactLifecycle.Record(contact, ContactLifecycle.Actions.Updated, "rep-1", Now, "summary");

        contact.UpdatedBy.Should().Be("rep-1");
        contact.UpdatedAt.Should().Be(Now);
        contact.History.Should().ContainSingle()
               .Which.Should().BeEquivalentTo(new { Action = "Updated", Detail = "summary", By = "rep-1", At = Now });
    }
}
