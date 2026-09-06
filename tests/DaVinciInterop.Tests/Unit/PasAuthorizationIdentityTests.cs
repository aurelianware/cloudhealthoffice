using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The authorization identity is what makes <c>BR-PAS-INQUIRE-001</c> a lifecycle
/// proof rather than two unrelated calls, so the extractor must be exactly right
/// about one thing: it reports what the PAYER issued, and nothing else.
///
/// The failure that would matter most is a silent one — an extractor that finds
/// nothing and lets the scenario quietly fall back to some identifier CHO chose
/// would still go green while proving nothing. So the cases below are mostly
/// about absence, ambiguity and the exact nesting PAS puts the number in.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class PasAuthorizationIdentityTests
{
    /// <summary>
    /// Builds a ClaimResponse item the way a PAS payer does: the decision on the
    /// adjudication's reviewAction extension, and the authorization number as
    /// reviewAction's <c>number</c> sub-extension — whose url is the bare token,
    /// not an absolute URL.
    /// </summary>
    private static ClaimResponse.ItemComponent Item(
        int sequence,
        string? reviewActionCode = "A1",
        string? authorizationNumber = null,
        string? administrationReferenceNumber = null)
    {
        var reviewAction = new Extension { Url = PasProtocol.ReviewActionExtension };

        if (reviewActionCode is not null)
        {
            reviewAction.Extension.Add(new Extension(
                PasProtocol.ReviewActionCodeExtension,
                new CodeableConcept(PasProtocol.X12ReviewActionSystem, reviewActionCode)));
        }

        if (authorizationNumber is not null)
        {
            reviewAction.Extension.Add(new Extension(
                PasProtocol.ReviewActionNumberSubExtension, new FhirString(authorizationNumber)));
        }

        var item = new ClaimResponse.ItemComponent
        {
            ItemSequence = sequence,
            Adjudication =
            {
                new ClaimResponse.AdjudicationComponent
                {
                    Category = new CodeableConcept("http://example.org/adjudication", "submitted"),
                    Extension = { reviewAction },
                },
            },
        };

        if (administrationReferenceNumber is not null)
        {
            item.Extension.Add(new Extension(
                PasProtocol.AdministrationReferenceNumberExtension,
                new FhirString(administrationReferenceNumber)));
        }

        return item;
    }

    private static ClaimResponse Response(params ClaimResponse.ItemComponent[] items)
    {
        var response = new ClaimResponse
        {
            Status = FinancialResourceStatusCodes.Active,
            Use = ClaimUseCode.Preauthorization,
            Outcome = ClaimProcessingCodes.Complete,
        };
        response.Item.AddRange(items);
        return response;
    }

    [Fact]
    public void Reads_the_authorization_number_from_the_reviewAction_number_sub_extension()
    {
        var identities = PasAuthorizationIdentityExtractor.From(
            Response(Item(1, "A1", authorizationNumber: "AUTH-0001")));

        identities.Should().ContainSingle();
        identities[0].Value.Should().Be("AUTH-0001");
        identities[0].Kind.Should().Be(PasAuthorizationIdentityKind.AuthorizationNumber);
        identities[0].ItemSequence.Should().Be(1);
        identities[0].SourcePath.Should().Contain("reviewAction")
            .And.Contain("number", "evidence must say where the correlation key actually lived");
    }

    [Fact]
    public void Reads_the_administration_reference_number_from_the_response_item()
    {
        var identities = PasAuthorizationIdentityExtractor.From(
            Response(Item(1, "A4", administrationReferenceNumber: "AUTH-PEND0001")));

        identities.Should().ContainSingle();
        identities[0].Value.Should().Be("AUTH-PEND0001");
        identities[0].Kind.Should().Be(PasAuthorizationIdentityKind.AdministrationReferenceNumber);
    }

    /// <summary>
    /// The number lives INSIDE reviewAction. An extractor that looked for the
    /// authorizationNumber extension on the response item — the placement PAS uses
    /// on the REQUEST side — would find nothing here, and the scenario would have
    /// no identity to inquire on.
    /// </summary>
    [Fact]
    public void Does_not_confuse_the_request_side_placement_with_the_response_side_placement()
    {
        var item = Item(1, "A1");
        item.Extension.Add(new Extension(
            PasProtocol.AuthorizationNumberExtension, new FhirString("NOT-ISSUED-HERE")));

        PasAuthorizationIdentityExtractor.From(Response(item))
            .Should().BeEmpty("the payer issues the number inside reviewAction, not on the item");
    }

    [Fact]
    public void Reports_no_identity_when_the_payer_issued_none()
    {
        var identities = PasAuthorizationIdentityExtractor.From(Response(Item(1, "A3")));

        identities.Should().BeEmpty();

        var selection = PasAuthorizationIdentityExtractor.Select(identities);
        selection.IsResolved.Should().BeFalse();
        selection.Selected.Should().BeNull();
        selection.Problem.Should().Contain("issued no authorization number");
    }

    [Fact]
    public void Reports_no_identity_for_a_null_claim_response()
    {
        PasAuthorizationIdentityExtractor.From(null).Should().BeEmpty();
        PasAuthorizationIdentityExtractor.SelectFrom(null).IsResolved.Should().BeFalse();
    }

    [Fact]
    public void Ignores_a_blank_authorization_number()
    {
        PasAuthorizationIdentityExtractor.From(Response(Item(1, "A1", authorizationNumber: "   ")))
            .Should().BeEmpty("an empty string is not an identity a payer issued");
    }

    /// <summary>
    /// One authorization covering several items repeats its number per item. That
    /// is one authorization, not a conflict.
    /// </summary>
    [Fact]
    public void Treats_the_same_number_repeated_across_items_as_one_authorization()
    {
        var selection = PasAuthorizationIdentityExtractor.SelectFrom(Response(
            Item(1, "A1", authorizationNumber: "AUTH-0001"),
            Item(2, "A1", authorizationNumber: "AUTH-0001")));

        selection.IsResolved.Should().BeTrue();
        selection.Selected!.Value.Should().Be("AUTH-0001");
        selection.Candidates.Should().HaveCount(2);
    }

    /// <summary>
    /// Two different authorization numbers mean the response describes two
    /// authorizations. A single inquiry cannot be about both, so the selection
    /// refuses rather than silently taking the first.
    /// </summary>
    [Fact]
    public void Refuses_to_choose_between_conflicting_authorization_numbers()
    {
        var selection = PasAuthorizationIdentityExtractor.SelectFrom(Response(
            Item(1, "A1", authorizationNumber: "AUTH-0001"),
            Item(2, "A1", authorizationNumber: "AUTH-0002")));

        selection.IsResolved.Should().BeFalse();
        selection.Selected.Should().BeNull();
        selection.Problem.Should().Contain("AUTH-0001").And.Contain("AUTH-0002");
        selection.Candidates.Should().HaveCount(2, "both are still reported, so evidence shows the conflict");
    }

    /// <summary>
    /// An authorization number names a decided authorization; an administration
    /// reference number is the handle for one still in progress. When a payer
    /// issued both, the decided one is what an inquiry should name.
    /// </summary>
    [Fact]
    public void Prefers_the_authorization_number_over_the_administration_reference_number()
    {
        var selection = PasAuthorizationIdentityExtractor.SelectFrom(Response(
            Item(1, "A1", authorizationNumber: "AUTH-0001", administrationReferenceNumber: "AUTH-PEND0001")));

        selection.IsResolved.Should().BeTrue();
        selection.Selected!.Kind.Should().Be(PasAuthorizationIdentityKind.AuthorizationNumber);
        selection.Selected.Value.Should().Be("AUTH-0001");
    }

    /// <summary>
    /// Differing administration reference numbers are only a conflict when no
    /// authorization number is present — otherwise the authorization number
    /// already settled which authorization the inquiry is about.
    /// </summary>
    [Fact]
    public void Conflict_is_judged_within_the_winning_kind_only()
    {
        var selection = PasAuthorizationIdentityExtractor.SelectFrom(Response(
            Item(1, "A1", authorizationNumber: "AUTH-0001", administrationReferenceNumber: "PEND-A"),
            Item(2, "A1", authorizationNumber: "AUTH-0001", administrationReferenceNumber: "PEND-B")));

        selection.IsResolved.Should().BeTrue();
        selection.Selected!.Value.Should().Be("AUTH-0001");
    }

    [Fact]
    public void Refuses_to_choose_between_conflicting_administration_reference_numbers()
    {
        var selection = PasAuthorizationIdentityExtractor.SelectFrom(Response(
            Item(1, "A4", administrationReferenceNumber: "PEND-A"),
            Item(2, "A4", administrationReferenceNumber: "PEND-B")));

        selection.IsResolved.Should().BeFalse();
        selection.Problem.Should().Contain("PEND-A").And.Contain("PEND-B");
    }

    /// <summary>
    /// The identity is a payer-issued handle, and evidence quotes it. It must
    /// carry no member or clinical content — which it cannot, being generated by
    /// the payer from a counter, but the summary is what reaches run.json so it is
    /// worth pinning that it says only what it claims to.
    /// </summary>
    [Fact]
    public void Safe_summary_states_the_kind_the_value_and_the_source_only()
    {
        var summary = PasAuthorizationIdentityExtractor.SafeSummary(
            PasAuthorizationIdentityExtractor.From(Response(Item(1, "A1", authorizationNumber: "AUTH-0001"))));

        summary.Should().Contain("AUTH-0001").And.Contain("AuthorizationNumber");
        summary.Should().NotContain(SyntheticInteropData.MemberId);
    }

    [Fact]
    public void Safe_summary_says_so_when_nothing_was_issued()
    {
        PasAuthorizationIdentityExtractor.SafeSummary(Array.Empty<PasAuthorizationIdentity>())
            .Should().Contain("no payer-issued authorization identity");
    }
}
