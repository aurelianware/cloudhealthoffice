using CloudHealthOffice.Infrastructure.Security;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

/// <summary>
/// Work a person raises and a person closes, with no source service behind
/// it: the follow-up from a contact (cho-service.md §8 step 3). It is the
/// first work type that is not source-owned, so what it must not break is as
/// much the point as what it does.
/// </summary>
public class PersonCreatedWorkTests
{
    private readonly WorkTestKit _kit = new();
    private const string T = WorkTestKit.Tenant;

    private static WorkIntakeService.AdHocWork Follow(
        string title = "Call back about the denial",
        string[]? reasons = null,
        Dictionary<string, List<string>>? facets = null,
        WorkType type = WorkType.Inquiry,
        string? openUrl = "/calls/contact-1",
        bool expedited = false,
        DateTime? deadline = null) =>
        new(T, type, "C-20261023-AAAA1111", title, openUrl, reasons ?? ["CLAIM_DENIAL"], facets ?? new(),
            "M1", null, "Medicare", expedited, deadline, "rep-1");

    [Fact]
    public async Task Stable_contact_request_key_reuses_work_without_overwriting_it()
    {
        var request = Follow() with { RequestKey = "contact-follow-1", ReceivedAt = DateTime.UtcNow };
        var first = await _kit.Intake().CreateAdHocAsync(request);
        var second = await _kit.Intake().CreateAdHocAsync(request);
        second.Id.Should().Be(first.Id);
        second.Version.Should().Be(first.Version);
        var act = async () => await _kit.Intake().CreateAdHocAsync(request with { Title = "Different work" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── What it creates ───────────────────────────────────────────

    [Fact]
    public async Task A_follow_up_lands_in_the_queue_its_reason_routes_to()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());

        item.QueueId.Should().Be(WorkTestKit.QueueId("inquiry.claim"));
        item.Status.Should().Be(WorkItemStatus.Open);
        item.Attributes.Title.Should().Be("Call back about the denial");
        item.SourceNumber.Should().Be("C-20261023-AAAA1111");
    }

    [Fact]
    public async Task A_reason_no_queue_claims_lands_in_the_follow_up_fallback()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow(reasons: ["SOMETHING_NEW"]));

        item.QueueId.Should().Be(WorkTestKit.QueueId("inquiry.other"));
    }

    [Fact]
    public async Task A_grievance_routes_on_its_classification_not_on_why_the_member_called()
    {
        // The whole reason facets exist: a benefits question can become a
        // grievance, and it is the classification that starts the clock.
        var item = await _kit.Intake().CreateAdHocAsync(Follow(
            reasons: ["BENEFITS"],
            facets: new() { [WorkFacets.Classification] = ["Grievance"] }));

        item.QueueId.Should().Be(WorkTestKit.QueueId("inquiry.complaint"));
    }

    [Fact]
    public async Task Without_the_classification_the_same_call_is_an_ordinary_follow_up()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow(reasons: ["BENEFITS"]));

        item.QueueId.Should().Be(WorkTestKit.QueueId("inquiry.eligibility"));
    }

    [Fact]
    public async Task Two_follow_ups_from_one_contact_are_two_pieces_of_work()
    {
        var first = await _kit.Intake().CreateAdHocAsync(Follow("Call the member back"));
        var second = await _kit.Intake().CreateAdHocAsync(Follow("Ask the provider for the records"));

        second.Id.Should().NotBe(first.Id);
        (await _kit.Store.GetItemAsync(T, first.Id))!.Attributes.Title.Should().Be("Call the member back");
    }

    [Fact]
    public async Task The_item_opens_at_the_contact_it_came_from()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());

        WorkEntitlements.OpenUrl(item).Should().Be("/calls/contact-1");
    }

    [Fact]
    public async Task Without_one_it_opens_the_work_list_rather_than_guessing_a_screen()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow(openUrl: null));

        WorkEntitlements.OpenUrl(item).Should().Be("/my-work");
    }

    [Theory]
    [InlineData("https://evil.test/steal")]
    [InlineData("//evil.test/steal")]
    [InlineData("/\\evil.test")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/calls/a\nb")]
    public async Task An_open_url_that_could_leave_the_portal_is_refused_and_falls_back(string hostile)
    {
        // The attributes reach work-management from another service, so the
        // "Open" button on My Work must not be able to send a rep off-site.
        WorkEntitlements.IsSafePortalPath(hostile).Should().BeFalse();

        var item = await _kit.Intake().CreateAdHocAsync(Follow(openUrl: hostile));
        WorkEntitlements.OpenUrl(item).Should().Be("/my-work");
    }

    [Fact]
    public async Task An_expedited_follow_up_outscores_a_routine_one_raised_at_the_same_moment()
    {
        var routine = await _kit.Intake().CreateAdHocAsync(Follow(reasons: ["AUTH_STATUS"]));
        var urgent = await _kit.Intake().CreateAdHocAsync(Follow(reasons: ["AUTH_STATUS"], expedited: true));

        urgent.PriorityScore.Should().BeGreaterThan(routine.PriorityScore);
    }

    [Fact]
    public async Task A_deadline_from_the_caller_beats_the_queue_sla()
    {
        var due = _kit.Now.AddHours(6);

        var item = await _kit.Intake().CreateAdHocAsync(Follow(deadline: due));

        item.DueAt.Should().Be(due);
    }

    [Fact]
    public async Task Reason_codes_are_upper_cased_so_a_screens_casing_does_not_change_the_queue()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow(reasons: ["  claim_denial  "]));

        item.Attributes.PrimaryPendCode.Should().Be("CLAIM_DENIAL");
        item.QueueId.Should().Be(WorkTestKit.QueueId("inquiry.claim"));
    }

    [Fact]
    public async Task A_facet_name_that_could_not_be_stored_is_dropped_rather_than_failing_the_call()
    {
        // A Mongo key cannot contain '.', and losing the follow-up because a
        // caller sent a bad facet name would be the worse failure.
        var item = await _kit.Intake().CreateAdHocAsync(Follow(
            facets: new() { ["bad.name"] = ["x"], [WorkFacets.Channel] = ["Phone"] }));

        item.Attributes.Facets.Should().ContainKey(WorkFacets.Channel);
        item.Attributes.Facets.Should().NotContainKey("bad.name");
    }

    // ── What it refuses ───────────────────────────────────────────

    [Theory]
    [InlineData(WorkType.Claim)]
    [InlineData(WorkType.Appeal)]
    [InlineData(WorkType.Enrollment834)]
    [InlineData(WorkType.Payment)]
    public async Task Source_owned_work_cannot_be_conjured_by_a_person(WorkType type)
    {
        // Otherwise anyone with the permission could put a claim in a queue
        // that no claim is actually pended for.
        await _kit.Intake().Invoking(i => i.CreateAdHocAsync(Follow(type: type)))
                  .Should().ThrowAsync<ArgumentException>();
    }

    // ── Completing it ─────────────────────────────────────────────

    [Fact]
    public async Task The_rep_who_holds_it_closes_it_and_the_reason_is_kept()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());
        var rep = new TestActor("rep-1", T, ChoRolePermissions.MemberServices);
        await _kit.Distribution().AssignAsync(new TestActor("sup", T, ChoRolePermissions.ClaimsSupervisor), item.Id, "rep-1");

        var result = await _kit.Distribution().CompleteAsync(rep, item.Id, "  Member called back, explained the denial  ");

        result.Status.Should().Be(DistributionStatus.Ok);
        var stored = (await _kit.Store.GetItemAsync(T, item.Id))!;
        stored.Status.Should().Be(WorkItemStatus.Done);
        stored.CompletedBy.Should().Be("rep-1");
        stored.Resolution.Should().Be("Member called back, explained the denial");
        (await _kit.Store.ListAuditAsync(T, item.Id)).Should().Contain(e => e.Action == WorkAuditActions.Completed);
    }

    [Fact]
    public async Task An_open_follow_up_nobody_holds_can_still_be_closed()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());

        var result = await _kit.Distribution()
            .CompleteAsync(new TestActor("rep-2", T, ChoRolePermissions.MemberServices), item.Id, null);

        result.Status.Should().Be(DistributionStatus.Ok);
    }

    [Fact]
    public async Task Someone_elses_lease_is_not_theirs_to_close()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());
        await _kit.Distribution().AssignAsync(new TestActor("sup", T, ChoRolePermissions.ClaimsSupervisor), item.Id, "rep-1");

        var result = await _kit.Distribution()
            .CompleteAsync(new TestActor("rep-2", T, ChoRolePermissions.MemberServices), item.Id, null);

        result.Status.Should().Be(DistributionStatus.Forbidden);
    }

    [Fact]
    public async Task A_claims_item_is_not_completed_from_the_work_list()
    {
        // Its source says when it is done; closing it here would leave the
        // queue disagreeing with the claim.
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));

        var result = await _kit.Distribution().CompleteAsync(
            new TestActor("examiner", T, ChoRolePermissions.ClaimsExaminer), WorkTestKit.ItemId("c1"), "done");

        result.Status.Should().Be(DistributionStatus.Conflict);
        result.Error.Should().Contain("completed by its source");
    }

    [Fact]
    public async Task A_role_without_the_contact_permission_cannot_close_a_follow_up()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());

        var result = await _kit.Distribution()
            .CompleteAsync(new TestActor("examiner", T, ChoRolePermissions.ClaimsExaminer), item.Id, null);

        result.Status.Should().Be(DistributionStatus.Forbidden);
    }

    [Fact]
    public async Task Closing_it_twice_is_refused_rather_than_rewriting_who_closed_it()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());
        var rep = new TestActor("rep-1", T, ChoRolePermissions.MemberServices);
        await _kit.Distribution().CompleteAsync(rep, item.Id, "first");

        var again = await _kit.Distribution()
            .CompleteAsync(new TestActor("rep-2", T, ChoRolePermissions.MemberServices), item.Id, "second");

        again.Status.Should().Be(DistributionStatus.Conflict);
        (await _kit.Store.GetItemAsync(T, item.Id))!.Resolution.Should().Be("first");
    }

    // ── It behaves like work ──────────────────────────────────────

    [Fact]
    public async Task A_rep_pulls_a_follow_up_with_get_next()
    {
        var item = await _kit.Intake().CreateAdHocAsync(Follow());
        await _kit.JoinAsync("rep-1", "inquiry.claim");

        var next = await _kit.Distribution()
            .GetNextAsync(new TestActor("rep-1", T, ChoRolePermissions.MemberServices), null);

        next!.Item!.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task An_examiner_is_not_handed_a_follow_up_even_with_the_membership()
    {
        // Entitlement is the role, and a membership can only narrow it.
        await _kit.Intake().CreateAdHocAsync(Follow());
        await _kit.JoinAsync("examiner", "inquiry.claim");

        var next = await _kit.Distribution()
            .GetNextAsync(new TestActor("examiner", T, ChoRolePermissions.ClaimsExaminer), null);

        next.Should().BeNull();
    }

    [Fact]
    public async Task A_reconcile_of_the_claims_it_knows_nothing_about_leaves_a_follow_up_alone()
    {
        // No source owns a follow-up, so the thing that closes stale claim
        // work must not reach it.
        var item = await _kit.Intake().CreateAdHocAsync(Follow());

        await _kit.Intake().ReconcileClaimsAsync(T, new FakeClaimsWorkSource(), 100);

        (await _kit.Store.GetItemAsync(T, item.Id))!.Status.Should().Be(WorkItemStatus.Open);
    }

    [Fact]
    public async Task Recompute_rescores_a_follow_up_as_its_due_date_approaches()
    {
        // Recompute used to walk Claim items only, so every other work type
        // kept the score it was born with however long it waited.
        var item = await _kit.Intake().CreateAdHocAsync(Follow());
        var born = item.PriorityScore;

        _kit.Clock.Advance(TimeSpan.FromDays(4));
        var result = await _kit.Intake().RecomputeAsync(T);

        result.Rescored.Should().BeGreaterThan(0);
        (await _kit.Store.GetItemAsync(T, item.Id))!.PriorityScore.Should().BeGreaterThan(born);
    }
}
