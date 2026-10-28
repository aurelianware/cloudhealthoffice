using System.Text.Json.Nodes;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Controllers;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using FhirService.Services.PayerToPayer.Outbound;
using FhirService.Services.PayerToPayer.Outbound.Bulk;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// G-35 outbound — bulk Payer-to-Payer with CHO as the NEW payer: many
/// transitioning members fetched from one prior payer over the PDex 2.1 bulk
/// operations (<c>Group/$bulk-member-match</c>, then
/// <c>Group/{id}/$davinci-data-export</c> on the matched Group).
///
/// The production <see cref="PayerToPayerBulkOutboundService"/> runs over the
/// real directory resolver, consent gate, member and coverage sources,
/// response validation and ingestion. Only the far side of the wire is a
/// double: <see cref="PdexPriorPayer"/> implements the transport seam
/// <see cref="IPayerToPayerBulkRemoteClient"/> and answers as a PDex payer
/// would — it reads the submitted Parameters, matches on the member number or
/// name and birth date, answers with PDex Groups (the submitted Patients
/// contained), and exports NDJSON. Synthetic data only.
/// </summary>
public class PayerToPayerBulkOutboundTests
{
    private const string TargetPayer = "PRIOR-PLAN";
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Harness(
        PayerToPayerBulkOutboundService Service,
        PdexPriorPayer Peer,
        InMemoryPayerToPayerBulkOutboundJobStore Store,
        IPayerToPayerImportRepository Imports,
        Clock Clock,
        MutableConsents Consents);

    /// <summary>A consent gate whose opted-in members can change mid-job.</summary>
    private sealed class MutableConsents(params string[] optedIn) : IPayerToPayerConsentGate
    {
        public HashSet<string> OptedIn { get; } = [.. optedIn];

        public Task<CloudHealthOffice.Consent.Contracts.ConsentDecision> EvaluateAsync(
            string tenantId, string memberId, DateTime? asOfUtc = null, CancellationToken ct = default)
            => AcceptanceContext.PayerToPayerConsentGate([.. OptedIn]).EvaluateAsync(tenantId, memberId, asOfUtc, ct);
    }

    private static Harness Build(PdexPriorPayer peer, string[] optedIn, int maxMembers = 500)
    {
        var provider = new MockPatientAccessDataProvider();
        var adapterOptions = Options.Create(new FhirAdapterOptions { TenantId = AcceptanceContext.TenantId });
        var directory = Options.Create(new PayerToPayerDirectoryOptions
        {
            LocalPayerId = "cloud-health-office",
            PayersByTenant = new()
            {
                [AcceptanceContext.TenantId] =
                [
                    new PayerToPayerEndpointEntry
                    {
                        PayerId = TargetPayer,
                        EndpointKey = "prior-plan-fhir",
                        BaseUrl = "https://prior-payer.example/fhir/r4",
                    },
                ],
            },
        });

        var clock = new Clock(Start);
        var store = new InMemoryPayerToPayerBulkOutboundJobStore();
        var imports = new InMemoryPayerToPayerImportRepository();
        var consents = new MutableConsents(optedIn);
        var service = new PayerToPayerBulkOutboundService(
            new PatientAccessPayerToPayerMemberSource(provider, adapterOptions),
            new PatientAccessPayerToPayerMemberMatchSource(provider, adapterOptions),
            consents,
            new ConfiguredPayerToPayerEndpointResolver(directory, AcceptanceContext.Logger<ConfiguredPayerToPayerEndpointResolver>()),
            peer,
            store,
            new PayerToPayerPackageIngestionService(
                imports,
                AcceptanceContext.Logger<PayerToPayerPackageIngestionService>(),
                new FhirService.Services.Clinical.ClinicalPayloadValidator()),
            directory,
            Options.Create(new PayerToPayerBulkOutboundOptions { MaxMembersPerRequest = maxMembers }),
            clock,
            AcceptanceContext.Logger<PayerToPayerBulkOutboundService>());

        return new Harness(service, peer, store, imports, clock, consents);
    }

    private static BulkOutboundStartRequest Request(params string[] members)
        => new(AcceptanceContext.TenantId, TargetPayer, members, AsOfDate: null, InitiatedBy: "client:enrollment");

    /// <summary>Advances the job until it finishes, moving the clock to each next attempt.</summary>
    private static async Task<PayerToPayerBulkOutboundJob> RunAsync(Harness h, string jobId, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var job = (await h.Store.GetAsync(AcceptanceContext.TenantId, jobId, h.Clock.Now))!;
            if (job.IsTerminal) return job;
            if (job.NextAttemptAt > h.Clock.Now) h.Clock.Now = job.NextAttemptAt;
            await h.Service.AdvanceAsync(job);
        }
        return (await h.Store.GetAsync(AcceptanceContext.TenantId, jobId, h.Clock.Now))!;
    }

    private static PayerToPayerBulkOutboundMember Member(PayerToPayerBulkOutboundJob job, string id)
        => job.Members.Single(m => m.MemberId == id);

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task OptedInMembers_AreMatchedExportedAndIngested_EachUnderTheirOwnLedgerEntry()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001", "pat-002"]);

        var started = await h.Service.StartAsync(Request("pat-001", "pat-002", "pat-003"));
        started.Refusal.Should().Be(BulkOutboundRefusal.None);
        h.Peer.Calls.Should().BeEmpty("nothing is sent until the worker advances the job");

        var job = await RunAsync(h, started.Job!.JobId);

        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Completed);
        h.Peer.Calls.Should().Equal(
            "bulk-member-match", "poll", "davinci-data-export:" + PdexPriorPayer.MatchedGroupId,
            "poll", "poll", "file", "file", "complete");

        // pat-003 has no opt-in: it was not sent at all.
        Member(job, "pat-003").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.NotAuthorized);
        h.Peer.SubmittedBundles.Should().HaveCount(2);

        var john = Member(job, "pat-001");
        john.Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Completed);
        john.RemoteMemberId.Should().Be("prior-1001");
        john.ReceivedResourceCount.Should().Be(4, "Patient, Coverage, one EOB and the Organization it names");

        var mary = Member(job, "pat-002");
        mary.Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Completed);
        mary.RemoteMemberId.Should().Be("prior-2002");
        mary.ReceivedResourceCount.Should().Be(2, "Patient and one EOB; no Organization of John's");
        mary.ExchangeId.Should().NotBe(john.ExchangeId);

        // The claim naming both members is kept by neither, and the stranger's is dropped.
        job.CrossMemberResourceCount.Should().Be(1);

        var johnLedger = await h.Imports.GetLedgerAsync(AcceptanceContext.TenantId, john.ExchangeId!);
        johnLedger!.Status.Should().Be(PayerToPayerIngestionStatus.Completed);
        johnLedger.MemberId.Should().Be("pat-001");
        johnLedger.SourcePayerId.Should().Be(TargetPayer);
        (await h.Imports.GetLedgerAsync(AcceptanceContext.TenantId, mary.ExchangeId!))!.MemberId.Should().Be("pat-002");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task TheMatchRequest_IsPdexShaped_AndDisclosesOnlyTheMatchingAttributes()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001", "pat-002"]);
        var started = await h.Service.StartAsync(Request("pat-001", "pat-002"));
        await RunAsync(h, started.Job!.JobId);

        var john = h.Peer.SubmittedBundles[0];
        var parts = (john["part"] as JsonArray)!.OfType<JsonObject>().ToDictionary(p => (string)p["name"]!);
        parts.Keys.Should().BeEquivalentTo(["MemberPatient", "CoverageToMatch", "Consent"]);

        var patient = parts["MemberPatient"]["resource"]!.AsObject();
        var patientJson = patient.ToJsonString();
        patientJson.Should().Contain("\"code\":\"MB\"").And.Contain("SUB-1001");
        patientJson.Should().Contain("Smith").And.Contain("John").And.Contain("1955-07-14").And.Contain("\"male\"");
        patientJson.Should().NotContain("Main St").And.NotContain("615-555").And.NotContain("37201");
        patientJson.Should().NotContain("pat-001", "CHO's own member id is not sent");

        var coverage = parts["CoverageToMatch"]["resource"]!.AsObject();
        ((string)coverage["subscriberId"]!).Should().Be("SUB-1001");
        ((string)coverage["payor"]![0]!["identifier"]!["value"]!).Should().Be(TargetPayer);

        var consent = parts["Consent"]["resource"]!.AsObject().ToJsonString();
        consent.Should().Contain("cloud-health-office").And.Contain(TargetPayer);

        // Mary holds no coverage with the payer: she is sent without a member number.
        var mary = (h.Peer.SubmittedBundles[1]["part"] as JsonArray)![0]!["resource"]!.ToJsonString();
        mary.Should().NotContain("\"MB\"").And.Contain("Jones");
    }

    // ── What the payer answers ──────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task NoMatchAndConsentConstrainedMembers_AreRecorded_AndNotExported()
    {
        var peer = new PdexPriorPayer { ConsentConstrained = ["SUB-1001"], Unknown = ["Jones"] };
        var h = Build(peer, optedIn: ["pat-001", "pat-002"]);

        var started = await h.Service.StartAsync(Request("pat-001", "pat-002"));
        var job = await RunAsync(h, started.Job!.JobId);

        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Completed);
        Member(job, "pat-001").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.ConsentConstrained);
        Member(job, "pat-002").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.NoMatch);
        h.Peer.Calls.Should().NotContain(c => c.StartsWith("davinci-data-export"), "no one matched");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task TwoMembersAnsweredAsOnePatient_AreNeitherExportedNorIngested()
    {
        var peer = new PdexPriorPayer { SameRemoteIdForAll = true };
        var h = Build(peer, optedIn: ["pat-001", "pat-002"]);

        var job = await RunAsync(h, (await h.Service.StartAsync(Request("pat-001", "pat-002"))).Job!.JobId);

        Member(job, "pat-001").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Ambiguous);
        Member(job, "pat-002").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Ambiguous);
        h.Peer.Calls.Should().NotContain(c => c.StartsWith("davinci-data-export"));
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task AMemberWhoOptsOutAfterTheMatch_HasNothingKept()
    {
        var peer = new PdexPriorPayer();
        var h = Build(peer, optedIn: ["pat-001", "pat-002"]);
        var started = await h.Service.StartAsync(Request("pat-001", "pat-002"));

        // Submit, then poll the match answer: the job is Matched, export not yet asked for.
        peer.OnMatchAnswered = () => h.Consents.OptedIn.Remove("pat-002");
        var job = await RunAsync(h, started.Job!.JobId);

        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Completed);
        Member(job, "pat-001").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Completed);
        Member(job, "pat-002").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.ConsentWithdrawn);
        Member(job, "pat-002").ExchangeId.Should().BeNull("nothing was ingested for her");
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task APayerThatCannotBeReached_IsTriedAgainLater_ThenTheJobCompletes()
    {
        var peer = new PdexPriorPayer { UnavailableSubmits = 2 };
        var h = Build(peer, optedIn: ["pat-001"]);
        var started = await h.Service.StartAsync(Request("pat-001"));

        var job = (await h.Store.GetAsync(AcceptanceContext.TenantId, started.Job!.JobId, Start))!;
        await h.Service.AdvanceAsync(job);
        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Pending);
        job.NextAttemptAt.Should().BeAfter(Start);
        Member(job, "pat-001").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Pending,
            "the member is checked again when the request is sent again");

        job = await RunAsync(h, job.JobId);
        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Completed);
        Member(job, "pat-001").Outcome.Should().Be(PayerToPayerBulkMemberOutcome.Completed);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task APayerThatNeverFinishes_TimesTheJobOut()
    {
        var peer = new PdexPriorPayer { MatchNeverFinishes = true };
        var h = Build(peer, optedIn: ["pat-001"]);

        var job = await RunAsync(h, (await h.Service.StartAsync(Request("pat-001"))).Job!.JobId, maxSteps: 200);

        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Failed);
        job.Failure.Should().Be(PayerToPayerBulkOutboundFailure.TimedOut);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task APayerThatRefusesTheToken_FailsTheJob()
    {
        var peer = new PdexPriorPayer { SubmitOutcome = RemoteCallOutcome.Unauthorized };
        var h = Build(peer, optedIn: ["pat-001"]);

        var job = await RunAsync(h, (await h.Service.StartAsync(Request("pat-001"))).Job!.JobId);

        job.Status.Should().Be(PayerToPayerBulkOutboundStatus.Failed);
        job.Failure.Should().Be(PayerToPayerBulkOutboundFailure.RemoteUnauthorized);
    }

    // ── Refused before anything is recorded ─────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task UnknownPayer_OtherTenant_NoMembers_OrTooMany_AreRefused()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001"], maxMembers: 2);

        (await h.Service.StartAsync(Request("pat-001") with { TargetPayerId = "NOT-IN-DIRECTORY" }))
            .Refusal.Should().Be(BulkOutboundRefusal.TargetPayerNotConfigured);
        (await h.Service.StartAsync(Request("pat-001") with { TenantId = "other-tenant" }))
            .Refusal.Should().Be(BulkOutboundRefusal.TenantMismatch);
        (await h.Service.StartAsync(Request(" ")))
            .Refusal.Should().Be(BulkOutboundRefusal.NoMembers);
        (await h.Service.StartAsync(Request("pat-001", "pat-002", "pat-003")))
            .Refusal.Should().Be(BulkOutboundRefusal.TooManyMembers);
        h.Peer.Calls.Should().BeEmpty();
    }

    // ── Worker ──────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task AClaimedJob_IsNotClaimedAgainUntilItsLeaseRunsOut()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001"]);
        await h.Service.StartAsync(Request("pat-001"));

        (await h.Store.ClaimDueAsync(Start, Start.AddMinutes(10), 5)).Should().HaveCount(1);
        (await h.Store.ClaimDueAsync(Start.AddMinutes(1), Start.AddMinutes(11), 5)).Should().BeEmpty();
        (await h.Store.ClaimDueAsync(Start.AddMinutes(11), Start.AddMinutes(21), 5)).Should().HaveCount(1);
    }

    // ── The endpoint ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task Initiate_Answers202WithAStatusUrl_AndThePollShowsEachMember()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001"]);
        var controller = Controller(h, enabled: true);

        var result = await controller.Initiate(new PayerToPayerBulkInitiateRequestDto
        {
            TargetPayerId = TargetPayer,
            MemberIds = ["pat-001", "pat-003"],
        }, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(202);
        var location = controller.Response.Headers["Content-Location"].ToString();
        location.Should().Contain("/PayerToPayer/$bulk-initiate/");
        var jobId = location[(location.LastIndexOf('/') + 1)..];

        await RunAsync(h, jobId);
        var status = await Controller(h, enabled: true).Status(jobId, CancellationToken.None);
        var receipt = status.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<Hl7.Fhir.Model.Parameters>().Subject;
        receipt.GetSingleValue<Hl7.Fhir.Model.FhirString>("status")!.Value.Should().Be("Completed");
        receipt.Parameter.Where(p => p.Name == "member").Should().HaveCount(2);
    }

    [Fact]
    [Trait("Scenario", "P2P-02")]
    [Trait("Backend", "Replace")]
    public async Task Initiate_IsNotServedUnlessEnabled_AndNeverForAPatientToken()
    {
        var h = Build(new PdexPriorPayer(), optedIn: ["pat-001"]);
        var body = new PayerToPayerBulkInitiateRequestDto { TargetPayerId = TargetPayer, MemberIds = ["pat-001"] };

        (await Controller(h, enabled: false).Initiate(body, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(501);

        var patientToken = Controller(h, enabled: true);
        patientToken.HttpContext.Items["SmartPatientId"] = "pat-001";
        (await patientToken.Initiate(body, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        h.Peer.Calls.Should().BeEmpty();
    }

    private static PayerToPayerBulkOutboundController Controller(Harness h, bool enabled)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new PayerToPayerBulkOutboundOptions { Enabled = enabled }))
            .AddSingleton<IPayerToPayerBulkOutboundService>(h.Service)
            .BuildServiceProvider();
        var controller = new PayerToPayerBulkOutboundController().WithTenant();
        controller.HttpContext.RequestServices = services;
        controller.HttpContext.Request.Scheme = "https";
        controller.HttpContext.Request.Host = new HostString("cho.example");
        return controller;
    }

    // ── The far side of the wire ────────────────────────────────────────────────

    /// <summary>
    /// A PDex prior payer at the transport seam. It knows John Smith by his
    /// member number (SUB-1001 → prior-1001) and Mary Jones by name and birth
    /// date (→ prior-2002), answers the match asynchronously, and exports
    /// NDJSON: each member's Patient and claims, an Organization only John's
    /// claim names, a claim naming both of them, and a stranger's claim.
    /// </summary>
    private sealed class PdexPriorPayer : IPayerToPayerBulkRemoteClient
    {
        public const string MatchedGroupId = "matched-1";
        private static readonly Uri MatchStatus = new("https://prior-payer.example/fhir/r4/bulk-match-status/1");
        private static readonly Uri ExportStatus = new("https://prior-payer.example/fhir/r4/export-status/1");

        private JsonObject? _answer;
        private int _exportPolls;

        public List<string> Calls { get; } = [];
        public List<JsonObject> SubmittedBundles { get; } = [];

        public HashSet<string> ConsentConstrained { get; init; } = [];
        public HashSet<string> Unknown { get; init; } = [];
        public bool SameRemoteIdForAll { get; init; }
        public bool MatchNeverFinishes { get; init; }
        public int UnavailableSubmits { get; set; }
        public RemoteCallOutcome SubmitOutcome { get; init; } = RemoteCallOutcome.Success;
        public Action? OnMatchAnswered { get; set; }

        public Task<BulkRemoteResponse> SubmitMatchAsync(PayerToPayerEndpoint endpoint, JsonObject parameters, CancellationToken ct = default)
        {
            Calls.Add("bulk-member-match");
            if (UnavailableSubmits > 0)
            {
                UnavailableSubmits--;
                return Task.FromResult(BulkRemoteResponse.Failed(RemoteCallOutcome.Unavailable));
            }
            if (SubmitOutcome != RemoteCallOutcome.Success)
                return Task.FromResult(BulkRemoteResponse.Failed(SubmitOutcome));

            SubmittedBundles.Clear();
            SubmittedBundles.AddRange((parameters["parameter"] as JsonArray)!.OfType<JsonObject>());
            _answer = Answer(SubmittedBundles);
            return Task.FromResult(BulkRemoteResponse.Accepted(MatchStatus));
        }

        public Task<BulkRemoteResponse> KickOffExportAsync(PayerToPayerEndpoint endpoint, string groupId, CancellationToken ct = default)
        {
            Calls.Add($"davinci-data-export:{groupId}");
            return Task.FromResult(BulkRemoteResponse.Accepted(ExportStatus));
        }

        public Task<BulkRemoteResponse> PollAsync(PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default)
        {
            Calls.Add("poll");
            if (statusUrl == MatchStatus)
            {
                if (MatchNeverFinishes) return Task.FromResult(BulkRemoteResponse.InProgress(TimeSpan.FromHours(1)));
                OnMatchAnswered?.Invoke();
                return Task.FromResult(BulkRemoteResponse.Completed(_answer!.ToJsonString()));
            }

            // The export takes one more poll to finish.
            if (_exportPolls++ == 0) return Task.FromResult(BulkRemoteResponse.InProgress(TimeSpan.FromSeconds(10)));
            var manifest = new JsonObject
            {
                ["transactionTime"] = "2026-10-28T09:00:00Z",
                ["requiresAccessToken"] = true,
                ["output"] = new JsonArray(
                    new JsonObject { ["type"] = "Patient", ["url"] = "https://prior-payer.example/files/patient.ndjson" },
                    new JsonObject { ["type"] = "ExplanationOfBenefit", ["url"] = "https://prior-payer.example/files/eob.ndjson" }),
                ["error"] = new JsonArray(),
            };
            return Task.FromResult(BulkRemoteResponse.Completed(manifest.ToJsonString()));
        }

        public Task<BulkRemoteResponse> DownloadAsync(
            PayerToPayerEndpoint endpoint, Uri fileUrl, bool requiresAccessToken, CancellationToken ct = default)
        {
            Calls.Add("file");
            var lines = fileUrl.AbsolutePath.EndsWith("patient.ndjson", StringComparison.Ordinal)
                ? new[]
                {
                    """{"resourceType":"Patient","id":"prior-1001","birthDate":"1955-07-14"}""",
                    """{"resourceType":"Patient","id":"prior-2002","birthDate":"1962-03-22"}""",
                }
                : new[]
                {
                    """{"resourceType":"Coverage","id":"cov-1001","status":"cancelled","beneficiary":{"reference":"Patient/prior-1001"},"payor":[{"reference":"Organization/org-1"}]}""",
                    """{"resourceType":"ExplanationOfBenefit","id":"eob-1001","status":"active","patient":{"reference":"Patient/prior-1001"},"provider":{"reference":"Organization/org-1"}}""",
                    """{"resourceType":"ExplanationOfBenefit","id":"eob-2002","status":"active","patient":{"reference":"Patient/prior-2002"}}""",
                    """{"resourceType":"ExplanationOfBenefit","id":"eob-both","status":"active","patient":{"reference":"Patient/prior-1001"},"subDetail":{"reference":"Patient/prior-2002"}}""",
                    """{"resourceType":"ExplanationOfBenefit","id":"eob-stranger","status":"active","patient":{"reference":"Patient/prior-9999"}}""",
                    """{"resourceType":"Organization","id":"org-1","name":"Prior Plan Clinic"}""",
                    "",
                };
            return Task.FromResult(BulkRemoteResponse.Completed(string.Join('\n', lines)));
        }

        public Task CompleteAsync(PayerToPayerEndpoint endpoint, Uri statusUrl, CancellationToken ct = default)
        {
            Calls.Add("complete");
            return Task.CompletedTask;
        }

        /// <summary>The PDex answer: the three Groups, each submitted Patient contained and pointed at.</summary>
        private JsonObject Answer(List<JsonObject> bundles)
        {
            var groups = new Dictionary<string, (JsonArray Contained, JsonArray Members)>
            {
                ["MatchedMembers"] = ([], []), ["NonMatchedMembers"] = ([], []), ["ConsentConstrainedMembers"] = ([], []),
            };
            for (var i = 0; i < bundles.Count; i++)
            {
                var patient = (JsonObject)(bundles[i]["part"] as JsonArray)!.OfType<JsonObject>()
                    .Single(p => (string)p["name"]! == "MemberPatient")["resource"]!.DeepClone();
                var json = patient.ToJsonString();
                var localId = $"p{i + 1}";
                patient["id"] = localId;

                string? remoteId = json.Contains("SUB-1001") ? "prior-1001"
                    : json.Contains("Jones") && json.Contains("1962-03-22") ? "prior-2002"
                    : null;
                if (SameRemoteIdForAll && remoteId is not null) remoteId = "prior-1001";

                var group = remoteId is null || Unknown.Any(json.Contains) ? "NonMatchedMembers"
                    : ConsentConstrained.Any(json.Contains) ? "ConsentConstrainedMembers"
                    : "MatchedMembers";

                groups[group].Contained.Add(patient);
                groups[group].Members.Add(new JsonObject
                {
                    ["entity"] = new JsonObject
                    {
                        ["extension"] = new JsonArray(new JsonObject
                        {
                            ["url"] = "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/base-ext-match-parameters",
                            ["valueReference"] = new JsonObject { ["reference"] = $"#{localId}" },
                        }),
                        ["reference"] = group == "MatchedMembers" ? $"Patient/{remoteId}" : $"#{localId}",
                    },
                });
            }

            var parameters = new JsonArray();
            foreach (var (name, (contained, members)) in groups)
            {
                if (name != "MatchedMembers" && members.Count == 0) continue;
                parameters.Add(new JsonObject
                {
                    ["name"] = name,
                    ["resource"] = new JsonObject
                    {
                        ["resourceType"] = "Group",
                        ["id"] = name == "MatchedMembers" ? MatchedGroupId : $"{name}-1",
                        ["contained"] = contained,
                        ["type"] = "person",
                        ["actual"] = true,
                        ["member"] = members,
                    },
                });
            }
            return new JsonObject { ["resourceType"] = "Parameters", ["parameter"] = parameters };
        }
    }
}
