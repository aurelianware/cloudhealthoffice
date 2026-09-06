using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The request CHO builds for <c>Claim/$inquire</c>, the response reader for what
/// comes back, and the canonical comparison that settles which side of the
/// <c>$inquire</c> naming discrepancy was wrong.
///
/// These are the parts <c>BR-PAS-INQUIRE-001</c> rests on that can be checked
/// without a container. What they must not do is quietly succeed on a malformed
/// answer: an empty result and a rejected request both have to be distinguishable
/// from a match, or the scenario could report continuity it never observed.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class PasInquiryExchangeTests
{
    private static readonly SyntheticInteropData.PasRequestedService Service =
        SyntheticInteropData.PasRequestedService.PriorAuthorizationRequired;

    private static readonly DateTimeOffset Created = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static Bundle InquiryBundle(string identity = "AUTH-0001", Patient? member = null) =>
        SyntheticInteropData.PasInquiryBundle(Created, identity, Service, "interop-inq-001", member);

    private static Claim InquiryClaim(Bundle bundle) =>
        bundle.Entry.Select(entry => entry.Resource).OfType<Claim>().Single();

    // ── Request construction ─────────────────────────────────────────────────

    /// <summary>
    /// The pinned payer, and the PAS inquiry request bundle profile, both require
    /// these. A request missing one is rejected before any correlation happens, so
    /// the scenario would fail without ever testing what it exists to test.
    /// </summary>
    [Fact]
    public void Inquiry_bundle_carries_what_the_pas_inquiry_request_profile_requires()
    {
        var bundle = InquiryBundle();

        bundle.Type.Should().Be(Bundle.BundleType.Collection);
        bundle.Identifier?.Value.Should().NotBeNullOrWhiteSpace();
        bundle.Timestamp.Should().NotBeNull();
        bundle.Meta!.Profile.Should().Contain(PasProtocol.InquiryRequestBundleProfile);
        bundle.Entry.Should().OnlyContain(entry => !string.IsNullOrWhiteSpace(entry.FullUrl));

        bundle.Entry[0].Resource.Should().BeOfType<Claim>(
            "the inquiry Claim must be the first entry");

        var claim = InquiryClaim(bundle);
        claim.Meta!.Profile.Should().Contain(PasProtocol.ClaimInquiryProfile);
        claim.Identifier.Should().ContainSingle("profile-claim-inquiry requires Claim.identifier 1..1");
        claim.Status.Should().Be(FinancialResourceStatusCodes.Active);
        claim.Use.Should().Be(ClaimUseCode.Preauthorization);
        claim.Created.Should().NotBeNullOrWhiteSpace();
        claim.Patient?.Reference.Should().NotBeNullOrWhiteSpace();
        claim.Insurer?.Reference.Should().NotBeNullOrWhiteSpace();
        claim.Provider?.Reference.Should().NotBeNullOrWhiteSpace();
        claim.Insurance.Should().ContainSingle().Which.Coverage.Reference.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The identity goes back exactly as the payer issued it, on Claim.item, which
    /// is where PAS contextualizes extension-authorizationNumber. Mutating it —
    /// trimming, upper-casing, prefixing — would break the correlation the whole
    /// scenario turns on.
    /// </summary>
    [Fact]
    public void Inquiry_quotes_the_payer_issued_identity_verbatim_on_claim_item()
    {
        const string issued = "AUTH-0001";

        var item = InquiryClaim(InquiryBundle(issued)).Item.Should().ContainSingle().Subject;

        var authorizationNumber = item.Extension
            .Single(extension => extension.Url == PasProtocol.AuthorizationNumberExtension)
            .Value.Should().BeOfType<FhirString>().Subject;

        authorizationNumber.Value.Should().Be(issued);
    }

    [Fact]
    public void Inquiry_carries_the_same_member_provider_and_payer_context_the_submit_used()
    {
        var submit = SyntheticInteropData.PasRequestBundle(Created, Service, "interop-pa-001");
        var inquiry = InquiryBundle();

        static (string?, string?, string?, string?) Context(Bundle bundle)
        {
            var claim = bundle.Entry.Select(entry => entry.Resource).OfType<Claim>().Single();
            return (claim.Patient?.Reference, claim.Insurer?.Reference, claim.Provider?.Reference,
                claim.Insurance.FirstOrDefault()?.Coverage?.Reference);
        }

        Context(inquiry).Should().Be(Context(submit),
            "a PAS payer scopes an inquiry by member, insurer and requestor, so they must be the same "
            + "identities the authorization was created under");

        static string?[] MemberIdentifiers(Bundle bundle) =>
            bundle.Entry.Select(entry => entry.Resource).OfType<Patient>()
                .SelectMany(patient => patient.Identifier).Select(id => id.Value).ToArray();

        MemberIdentifiers(inquiry).Should().BeEquivalentTo(MemberIdentifiers(submit));
    }

    /// <summary>
    /// The pinned payer requires a Patient carrying an MB-typed identifier before
    /// it will process an inquiry at all.
    /// </summary>
    [Fact]
    public void Inquiry_carries_a_member_with_the_mb_typed_identifier_the_profile_requires()
    {
        var patient = InquiryBundle().Entry
            .Select(entry => entry.Resource).OfType<Patient>().Single();

        patient.Identifier.Should().Contain(identifier =>
            identifier.Type != null
            && identifier.Type.Coding.Any(coding =>
                coding.System == "http://terminology.hl7.org/CodeSystem/v2-0203" && coding.Code == "MB"));
    }

    [Fact]
    public void A_mismatched_corroborating_member_replaces_only_the_member()
    {
        var mismatched = InquiryBundle(member: SyntheticInteropData.OtherMember());

        var patient = mismatched.Entry.Select(entry => entry.Resource).OfType<Patient>().Single();
        patient.Identifier.Select(id => id.Value).Should().Contain(SyntheticInteropData.OtherMemberId);
        patient.Identifier.Select(id => id.Value).Should().NotContain(SyntheticInteropData.MemberId);

        var claim = InquiryClaim(mismatched);
        claim.Provider!.Reference.Should().Be(SyntheticInteropData.ProviderFullUrl,
            "only the corroborating member changes, so a refusal is attributable to the member");
        claim.Insurer!.Reference.Should().Be(SyntheticInteropData.InsurerFullUrl);
    }

    /// <summary>
    /// Both PAS operations take a single input parameter named <c>resource</c>,
    /// so the wrapper is shared. If it were named anything else the payer would
    /// reject the call outright.
    /// </summary>
    [Fact]
    public void Inquiry_is_wrapped_in_the_single_resource_parameter_pas_defines()
    {
        var parameters = SyntheticInteropData.AsInquiryParameters(InquiryBundle());

        parameters.Parameter.Should().ContainSingle()
            .Which.Name.Should().Be(PasProtocol.ResourceParameter);
        parameters.Parameter[0].Resource.Should().BeOfType<Bundle>();
    }

    [Fact]
    public void Inquiry_request_serializes_with_the_fhir_serializer_cho_runs()
    {
        var json = new FhirJsonSerializer()
            .SerializeToString(SyntheticInteropData.AsInquiryParameters(InquiryBundle()));

        json.Should().Contain(PasProtocol.AuthorizationNumberExtension).And.Contain("AUTH-0001");
    }

    /// <summary>
    /// The submit request the inquiry chains from must engage the payer's rules,
    /// or the payer decides nothing and issues no identity to inquire on. The
    /// default request deliberately does not, and must stay that way — that is
    /// what makes BR-PAS-SUBMIT-001 content-independent.
    /// </summary>
    [Fact]
    public void Only_the_rule_engaging_request_carries_the_upstream_payer_identifier()
    {
        static string?[] PayerIdentifiers(Bundle bundle) =>
            bundle.Entry.Select(entry => entry.Resource).OfType<Organization>()
                .Where(organization => organization.Id == SyntheticInteropData.PayerId)
                .SelectMany(organization => organization.Identifier)
                .Select(identifier => identifier.Value).ToArray();

        PayerIdentifiers(SyntheticInteropData.PasRequestBundle(Created))
            .Should().NotContain(SyntheticInteropData.UpstreamRulePayerIdentifierValue);

        PayerIdentifiers(SyntheticInteropData.PasRequestBundle(Created, Service, "interop-pa-001"))
            .Should().Contain(SyntheticInteropData.UpstreamRulePayerIdentifierValue);
    }

    [Fact]
    public void The_default_submit_request_is_unchanged_by_the_parameterized_overload()
    {
        var serializer = new FhirJsonSerializer();

        serializer.SerializeToString(SyntheticInteropData.PasRequestBundle(Created))
            .Should().Be(serializer.SerializeToString(SyntheticInteropData.PasRequestBundle(
                Created,
                SyntheticInteropData.PasRequestedService.OfficeVisit,
                SyntheticInteropData.PriorAuthId)));
    }

    // ── Response reading ─────────────────────────────────────────────────────

    private static Bundle ResponseBundle(
        string? authorizationNumber = "AUTH-0001",
        string reviewActionCode = "A1",
        string claimResponseId = "1808",
        string requestReference = "Claim/1807",
        string? profile = PasProtocol.InquiryResponseBundleProfile)
    {
        var reviewAction = new Extension { Url = PasProtocol.ReviewActionExtension };
        reviewAction.Extension.Add(new Extension(
            PasProtocol.ReviewActionCodeExtension,
            new CodeableConcept(PasProtocol.X12ReviewActionSystem, reviewActionCode)));

        if (authorizationNumber is not null)
        {
            reviewAction.Extension.Add(new Extension(
                PasProtocol.ReviewActionNumberSubExtension, new FhirString(authorizationNumber)));
        }

        var claimResponse = new ClaimResponse
        {
            Id = claimResponseId,
            Meta = new Meta { Profile = [PasProtocol.ClaimResponseProfile] },
            Status = FinancialResourceStatusCodes.Active,
            Use = ClaimUseCode.Preauthorization,
            Outcome = ClaimProcessingCodes.Complete,
            Request = new ResourceReference(requestReference),
            Item =
            {
                new ClaimResponse.ItemComponent
                {
                    ItemSequence = 1,
                    Adjudication =
                    {
                        new ClaimResponse.AdjudicationComponent { Extension = { reviewAction } },
                    },
                },
            },
        };

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Meta = profile is null ? null : new Meta { Profile = [profile] },
            Entry = { new Bundle.EntryComponent { Resource = claimResponse } },
        };
    }

    private static Parameters InquiryResult(params Bundle[] bundles)
    {
        var parameters = new Parameters();
        foreach (var bundle in bundles)
        {
            parameters.Parameter.Add(new Parameters.ParameterComponent
            {
                Name = PasProtocol.ResponseBundleParameter,
                Resource = bundle,
            });
        }

        return parameters;
    }

    [Fact]
    public void Reads_each_response_bundle_as_an_authorization()
    {
        var response = PasInquiryResponse.From(InquiryResult(ResponseBundle()))!;

        response.IsEmpty.Should().BeFalse();
        response.Matches.Should().ContainSingle();
        response.Matches[0].ClaimResponseId.Should().Be("1808");
        response.Matches[0].RequestReference.Should().Be("Claim/1807");
        response.ProtocolViolations().Should().BeEmpty();
    }

    [Fact]
    public void Correlates_a_match_by_the_payer_issued_identity_rather_than_by_position()
    {
        var response = PasInquiryResponse.From(InquiryResult(
            ResponseBundle(authorizationNumber: "AUTH-0002", claimResponseId: "1900"),
            ResponseBundle(authorizationNumber: "AUTH-0001", claimResponseId: "1808")))!;

        var carrying = response.MatchesCarrying("AUTH-0001");

        carrying.Should().ContainSingle();
        carrying[0].ClaimResponseId.Should().Be("1808", "the second entry is the one that matches");
        response.MatchesCarrying("AUTH-9999").Should().BeEmpty();
    }

    /// <summary>
    /// Matching nothing is a conformant answer — it is exactly what a payer must
    /// say when the corroborating context does not entitle the caller — so the
    /// reader must report it as an empty result, not as a parse failure.
    /// </summary>
    [Fact]
    public void An_empty_result_is_a_valid_answer_not_a_failure()
    {
        var response = PasInquiryResponse.From(new Parameters())!;

        response.Should().NotBeNull();
        response.IsEmpty.Should().BeTrue();
        response.ProtocolViolations().Should().BeEmpty();
        response.SafeSummary().Should().Contain("0 authorizations matched");
    }

    [Fact]
    public void Surfaces_an_operation_outcome_the_payer_attached_to_an_empty_result()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent
        {
            Name = "outcome",
            Resource = new OperationOutcome
            {
                Issue =
                {
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Information,
                        Code = OperationOutcome.IssueType.NotFound,
                        Diagnostics = "No matching authorization",
                    },
                },
            },
        });

        var response = PasInquiryResponse.From(parameters)!;

        response.Outcomes.Should().ContainSingle();
        response.SafeSummary().Should().Contain("No matching authorization");
    }

    [Fact]
    public void Reports_a_response_that_is_not_parameters_as_unparseable()
    {
        PasInquiryResponse.From(new OperationOutcome()).Should().BeNull();
        PasInquiryResponse.From(null).Should().BeNull();
    }

    /// <summary>
    /// A payer that answers an inquiry with the submit response bundle profile has
    /// used a defensible profile rather than a wrong one, so it is not a
    /// violation — the scenario records which profile was used as a finding.
    /// </summary>
    [Fact]
    public void Accepts_either_pas_response_bundle_profile_and_reports_which_was_used()
    {
        var reused = PasInquiryResponse.From(InquiryResult(
            ResponseBundle(profile: PasProtocol.ResponseBundleProfile)))!;

        reused.ProtocolViolations().Should().BeEmpty();
        reused.DeclaredBundleProfiles().Should().ContainSingle()
            .Which.Should().Be(PasProtocol.ResponseBundleProfile);
    }

    [Fact]
    public void Reports_a_response_bundle_declaring_no_pas_profile_at_all()
    {
        PasInquiryResponse.From(InquiryResult(ResponseBundle(profile: null)))!
            .ProtocolViolations().Should().ContainSingle()
            .Which.Should().Contain("declares neither");
    }

    [Fact]
    public void Reports_a_response_bundle_carrying_no_claim_response()
    {
        var empty = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Meta = new Meta { Profile = [PasProtocol.InquiryResponseBundleProfile] },
        };

        PasInquiryResponse.From(InquiryResult(empty))!
            .ProtocolViolations().Should().Contain(problem => problem.Contains("no ClaimResponse"));
    }

    [Fact]
    public void Reports_a_matched_authorization_that_is_not_a_preauthorization()
    {
        var bundle = ResponseBundle();
        bundle.Entry.Select(entry => entry.Resource).OfType<ClaimResponse>().Single().Use = ClaimUseCode.Claim;

        PasInquiryResponse.From(InquiryResult(bundle))!
            .ProtocolViolations().Should().Contain(problem => problem.Contains("not 'preauthorization'"));
    }

    [Fact]
    public void Safe_summary_describes_matches_without_member_or_clinical_content()
    {
        var summary = PasInquiryResponse.From(InquiryResult(ResponseBundle()))!.SafeSummary();

        summary.Should().Contain("1 authorization").And.Contain("AUTH-0001").And.Contain("A1");
        summary.Should().NotContain(SyntheticInteropData.MemberId);
        summary.Should().NotContain(Service.BillingCode);
    }

    // ── Canonical comparison ─────────────────────────────────────────────────

    /// <summary>
    /// The canonical the harness holds is the one the PAS IG publishes, and it is
    /// deliberately not derivable from the operation code. These constants are the
    /// whole basis of the assertion that closed the discrepancy, so they are
    /// pinned as literals.
    /// </summary>
    [Fact]
    public void Pins_the_pas_operation_canonicals_the_ig_publishes()
    {
        PasProtocol.SubmitOperationCanonical.Should()
            .Be("http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-submit");

        PasProtocol.InquiryOperationCanonical.Should()
            .Be("http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquiry");

        PasProtocol.UnpublishedInquiryCanonical.Should()
            .Be("http://hl7.org/fhir/us/davinci-pas/OperationDefinition/Claim-inquire");

        PasProtocol.InquiryOperationCanonical.Should().NotBe(PasProtocol.UnpublishedInquiryCanonical);
    }

    /// <summary>
    /// The operation code is what appears in the URL and is NOT the tail of the
    /// canonical. Deriving one from the other is precisely the mistake that
    /// produced the canonical CHO used to advertise.
    /// </summary>
    [Fact]
    public void The_inquiry_operation_code_and_canonical_deliberately_differ()
    {
        PasProtocol.InquiryOperationCode.Should().Be("inquire");
        PasProtocol.InquiryOperationCanonical.Should().EndWith("Claim-inquiry");

        PasProtocol.InquiryOperationCanonical.Should().NotEndWith(
            $"Claim-{PasProtocol.InquiryOperationCode}",
            "spelling the canonical from the operation code is what produced the wrong value");

        PasProtocol.SubmitOperationCanonical.Should().EndWith($"Claim-{PasProtocol.SubmitOperationCode}",
            "submit is the case where the two DO coincide, which is why the difference went unnoticed");
    }

    /// <summary>
    /// CHO's real production CapabilityStatement must name the published
    /// canonical. Asserted here, in the interop harness, as well as in the CMS
    /// acceptance suite: this is the value an independent implementation compares
    /// against, and a regression would silently reopen the discrepancy.
    /// </summary>
    [Fact]
    public void Cho_advertises_the_published_inquiry_canonical()
    {
        ChoPasSurface.ClaimOperations()
            .Single(operation => operation.Name == PasProtocol.InquiryOperationCode)
            .Definition.Should().Be(PasProtocol.InquiryOperationCanonical);
    }

    [Fact]
    public void Cho_advertises_the_published_submit_canonical()
    {
        ChoPasSurface.ClaimOperations()
            .Single(operation => operation.Name == PasProtocol.SubmitOperationCode)
            .Definition.Should().Be(PasProtocol.SubmitOperationCanonical);
    }
}
