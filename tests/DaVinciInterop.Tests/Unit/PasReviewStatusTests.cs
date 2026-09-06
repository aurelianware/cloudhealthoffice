using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Status continuity is the claim <c>BR-PAS-INQUIRE-001</c> makes about state, so
/// the normalization behind it must not flatten distinctions to make equality
/// easy, and must not invent a state for a code it does not recognize.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class PasReviewStatusTests
{
    private static ClaimResponse Response(params (int Sequence, string? Code)[] items)
    {
        var response = new ClaimResponse();

        foreach (var (sequence, code) in items)
        {
            var reviewAction = new Extension { Url = PasProtocol.ReviewActionExtension };
            if (code is not null)
            {
                reviewAction.Extension.Add(new Extension(
                    PasProtocol.ReviewActionCodeExtension,
                    new CodeableConcept
                    {
                        Coding =
                        {
                            new Coding(PasProtocol.X12ReviewActionSystem, code)
                            {
                                Display = $"display for {code}",
                            },
                        },
                    }));
            }

            response.Item.Add(new ClaimResponse.ItemComponent
            {
                ItemSequence = sequence,
                Adjudication =
                {
                    new ClaimResponse.AdjudicationComponent { Extension = { reviewAction } },
                },
            });
        }

        return response;
    }

    [Theory]
    [InlineData("A1", PasDisposition.Approved)]
    [InlineData("A2", PasDisposition.Denied)]
    [InlineData("A3", PasDisposition.NotRequired)]
    [InlineData("A4", PasDisposition.Pended)]
    [InlineData("A6", PasDisposition.Modified)]
    [InlineData("C", PasDisposition.Cancelled)]
    public void Maps_the_review_action_codes_the_pinned_implementations_emit(string code, PasDisposition expected)
    {
        PasReviewStatus.From(Response((1, code)))
            .Single().Disposition.Should().Be(expected);
    }

    /// <summary>
    /// The X12 306 code list is licensed and is not redistributed inside the IG
    /// package, so the table covers only the codes the pinned implementations
    /// actually emit. An unrecognized code must surface as unknown rather than be
    /// bucketed into a neighbouring state — guessing here would let a scenario
    /// claim continuity between two states it never understood.
    /// </summary>
    [Fact]
    public void Reports_an_unrecognized_code_as_unknown_rather_than_guessing()
    {
        var action = PasReviewStatus.From(Response((1, "A5"))).Single();

        action.Disposition.Should().Be(PasDisposition.Unknown);
        action.Code.Should().Be("A5", "the raw wire value is preserved for evidence");
    }

    [Fact]
    public void Reports_an_adjudicated_item_carrying_no_review_action_as_absent()
    {
        var action = PasReviewStatus.From(Response((1, null))).Single();

        action.Disposition.Should().Be(PasDisposition.Absent);
        action.ItemSequence.Should().Be(1, "the item is still reported, so its silence is visible");
    }

    [Fact]
    public void Preserves_the_raw_system_code_and_display()
    {
        var action = PasReviewStatus.From(Response((1, "A1"))).Single();

        action.System.Should().Be(PasProtocol.X12ReviewActionSystem);
        action.Code.Should().Be("A1");
        action.Display.Should().Be("display for A1");
    }

    [Fact]
    public void Only_a_pend_is_treated_as_a_state_the_payer_may_advance_on_its_own()
    {
        PasReviewStatus.From(Response((1, "A4"))).Single().IsSelfAdvancing.Should().BeTrue();

        foreach (var settled in new[] { "A1", "A2", "A3", "A6", "C" })
        {
            PasReviewStatus.From(Response((1, settled))).Single().IsSelfAdvancing
                .Should().BeFalse("{0} is a settled decision", settled);
        }
    }

    [Fact]
    public void Same_decision_holds_when_every_item_carries_the_same_code()
    {
        PasReviewStatus.SameDecision(
                PasReviewStatus.From(Response((1, "A1"), (2, "A4"))),
                PasReviewStatus.From(Response((1, "A1"), (2, "A4"))))
            .Should().BeTrue();
    }

    [Fact]
    public void Same_decision_fails_when_an_item_changed_code()
    {
        PasReviewStatus.SameDecision(
                PasReviewStatus.From(Response((1, "A4"))),
                PasReviewStatus.From(Response((1, "A1"))))
            .Should().BeFalse("a pend that became a certification is a state change, not continuity");
    }

    [Fact]
    public void Same_decision_fails_when_an_item_disappeared()
    {
        PasReviewStatus.SameDecision(
                PasReviewStatus.From(Response((1, "A1"), (2, "A1"))),
                PasReviewStatus.From(Response((1, "A1"))))
            .Should().BeFalse();
    }

    /// <summary>
    /// Display wording is the payer's prose. A payer that rephrases it between two
    /// operations has not changed the authorization, so continuity must not be
    /// asserted on it.
    /// </summary>
    [Fact]
    public void Same_decision_ignores_display_text()
    {
        var submitted = PasReviewStatus.From(Response((1, "A1")));

        var rephrased = new ClaimResponse();
        rephrased.Item.Add(new ClaimResponse.ItemComponent
        {
            ItemSequence = 1,
            Adjudication =
            {
                new ClaimResponse.AdjudicationComponent
                {
                    Extension =
                    {
                        new Extension
                        {
                            Url = PasProtocol.ReviewActionExtension,
                            Extension =
                            {
                                new Extension(
                                    PasProtocol.ReviewActionCodeExtension,
                                    new CodeableConcept
                                    {
                                        Coding =
                                        {
                                            new Coding(PasProtocol.X12ReviewActionSystem, "A1")
                                            {
                                                Display = "Approved in full",
                                            },
                                        },
                                    }),
                            },
                        },
                    },
                },
            },
        });

        PasReviewStatus.SameDecision(submitted, PasReviewStatus.From(rephrased)).Should().BeTrue();
    }

    [Fact]
    public void For_item_finds_the_action_by_sequence()
    {
        var actions = PasReviewStatus.From(Response((1, "A1"), (2, "A4")));

        PasReviewStatus.ForItem(actions, 2)!.Code.Should().Be("A4");
        PasReviewStatus.ForItem(actions, 3).Should().BeNull();
    }

    [Fact]
    public void Safe_summary_names_items_and_codes_and_nothing_else()
    {
        var summary = PasReviewStatus.SafeSummary(PasReviewStatus.From(Response((1, "A1"))));

        summary.Should().Contain("item 1").And.Contain("A1").And.Contain("Approved");
        summary.Should().NotContain(SyntheticInteropData.MemberId);
    }

    [Fact]
    public void Safe_summary_says_so_when_nothing_was_adjudicated()
    {
        PasReviewStatus.SafeSummary(PasReviewStatus.From(new ClaimResponse()))
            .Should().Be("(no adjudicated items)");
    }

    [Fact]
    public void Reads_nothing_from_a_null_claim_response()
    {
        PasReviewStatus.From(null).Should().BeEmpty();
    }
}
