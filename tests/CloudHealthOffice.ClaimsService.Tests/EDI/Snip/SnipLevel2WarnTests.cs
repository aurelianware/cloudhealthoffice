using System.Collections.Concurrent;
using ClaimsService.Controllers;
using ClaimsService.EDI.Inbound;
using ClaimsService.EDI.Validation;
using ClaimsService.Fhir;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using static CloudHealthOffice.ClaimsService.Tests.EDI.Snip.Snip837Samples;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// Go-live SNIP strictness: Level 2 warns (accepted, IK3/IK4 + IK5 E in the
/// 999, logged and recorded per submitter), Levels 1, 3 and 4 still reject,
/// and <see cref="Snip837ValidationOptions.PartnerOverrides"/> moves one
/// submitter's Level 2 back to Reject without affecting anyone else.
/// </summary>
public class SnipLevel2WarnTests
{
    private const string OtherSubmitter = "SUB002";

    private static SnipValidationResult Validate(string edi, Snip837ValidationOptions? options = null)
        => new X12837SnipValidator(options).Validate(edi);

    private static List<X12Segment> Ack(SnipValidationResult result)
        => X12Tokenizer.Tokenize(X12999AcknowledgmentBuilder.Build(result, new X12999AcknowledgmentBuilder.Options { ControlNumber = 7 })!).Segments.ToList();

    private static string Seg(X12Segment s) => s.Id + "*" + string.Join('*', s.Elements);

    /// <summary>837P with a claim-level (2300) DTP*472 — rule L2-2300-DTP472, at segment 17.</summary>
    private static List<string> ClaimLevelDtp472Body(string claimId = "PCN0001")
    {
        var body = ProfessionalBody(claimId);
        body.Insert(body.FindIndex(s => s.StartsWith("HI*", StringComparison.Ordinal)), "DTP*472*D8*20260110");
        return body;
    }

    private static string ClaimLevelDtp472(string claimId = "PCN0001") => Wrap(ProfessionalVersion, ClaimLevelDtp472Body(claimId));

    /// <summary>The same file sent by another submitter (ISA06 and GS02).</summary>
    private static string FromSubmitter(string edi, string submitter) => edi
        .Replace("*ZZ*SUB001         *", $"*ZZ*{submitter.PadRight(15)}*", StringComparison.Ordinal)
        .Replace("GS*HC*SUB001*", $"GS*HC*{submitter}*", StringComparison.Ordinal);

    private static Snip837ValidationOptions WithLevel2RejectFor(string key) => new()
    {
        PartnerOverrides = { [key] = new SnipLevelOverrides { Level2 = SnipAction.Reject } },
    };

    // ── Defaults ─────────────────────────────────────────────────────

    [Fact]
    public void Defaults_Level2Warns_OthersReject()
    {
        var options = new Snip837ValidationOptions();
        Assert.Equal(
            (SnipAction.Reject, SnipAction.Warn, SnipAction.Reject, SnipAction.Reject, SnipAction.Warn),
            (options.Level1, options.Level2, options.Level3, options.Level4, options.Level5));
    }

    [Fact]
    public void Level2Finding_IsAcceptedWithErrors_And999CarriesIk3Ik4()
    {
        var result = Validate(ClaimLevelDtp472());

        var issue = Assert.Single(result.AllIssues);
        Assert.Equal(("L2-2300-DTP472", SnipLevel.ImplementationGuide, SnipSeverity.Warning, "DTP", 17, "2300"),
            (issue.RuleId, issue.Level, issue.Severity, issue.SegmentId, issue.SegmentPosition, issue.Loop));
        Assert.Equal("E", result.AcknowledgmentCode);
        Assert.Single(result.AcceptedTransactionSets);
        Assert.Null(result.TransactionSets.Single().PartnerOverrideKey);

        var ack = Ack(result).Select(Seg).ToList();
        Assert.Contains("IK3*DTP*17*2300*I4", ack);
        Assert.Contains("CTX*CLM01:PCN0001", ack);
        Assert.Contains(ack, s => s.StartsWith("IK5*E*", StringComparison.Ordinal));
        Assert.Contains(ack, s => s.StartsWith("AK9*E*1*1*1", StringComparison.Ordinal));
    }

    [Fact]
    public void Level2ElementFinding_999HasIk4()
    {
        // CLM05-2 facility code qualifier must be B (837P): an element-level L2 finding.
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:A:1*Y*A*Y*Y")));

        var issue = Assert.Single(result.AllIssues, i => i.RuleId == "L2-CLM05-2");
        Assert.Equal(SnipSeverity.Warning, issue.Severity);
        Assert.True(result.TransactionSets.Single().Accepted);
        Assert.Contains(Ack(result), s => s.Id == "IK4" && s.Elements[0] == "5:2");
    }

    [Fact]
    public void Level1_StillRejectsByDefault()
    {
        var result = Validate(Professional().Replace("SE*24*0001", "SE*17*0001"));
        Assert.Equal(SnipSeverity.Error, Assert.Single(result.AllIssues, i => i.RuleId == "L1-SE01").Severity);
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Empty(result.AcceptedTransactionSets);
    }

    [Fact]
    public void Level3_StillRejectsByDefault()
    {
        var result = Validate(Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y")));
        Assert.Equal(SnipSeverity.Error, Assert.Single(result.AllIssues, i => i.RuleId == "L3-CLM-BALANCE").Severity);
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    [Fact]
    public void Level4_StillRejectsByDefault()
    {
        var result = Validate(Professional(b => b.RemoveSegment("DTP*472")));
        Assert.Equal(SnipSeverity.Error, Assert.Single(result.AllIssues, i => i.RuleId == "L4-DTP472").Severity);
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    [Fact]
    public void Level2Warning_DoesNotMaskALevel4RejectInTheSameSet()
    {
        var result = Validate(Wrap(ProfessionalVersion, Mutated(ClaimLevelDtp472Body(), b => b.RemoveSegment("LX*2"))));
        Assert.Contains(result.AllIssues, i => i.RuleId == "L2-2300-DTP472" && i.Severity == SnipSeverity.Warning);
        Assert.Equal("R", result.AcknowledgmentCode);
    }

    private static List<string> Mutated(List<string> body, Action<List<string>> mutate)
    {
        mutate(body);
        return body;
    }

    // ── Per-partner overrides ────────────────────────────────────────

    [Fact]
    public void PartnerOverride_FlipsLevel2ToReject_ForThatSubmitterOnly()
    {
        var options = WithLevel2RejectFor("SUB001");

        var flipped = Validate(ClaimLevelDtp472(), options);
        Assert.Equal(SnipSeverity.Error, Assert.Single(flipped.AllIssues).Severity);
        Assert.Equal("R", flipped.AcknowledgmentCode);
        Assert.Equal("SUB001", flipped.TransactionSets.Single().PartnerOverrideKey);
        Assert.Contains(Ack(flipped), s => Seg(s).StartsWith("IK5*R*", StringComparison.Ordinal));

        var other = Validate(FromSubmitter(ClaimLevelDtp472(), OtherSubmitter), options);
        Assert.Equal(SnipSeverity.Warning, Assert.Single(other.AllIssues).Severity);
        Assert.Equal("E", other.AcknowledgmentCode);
        Assert.Null(other.TransactionSets.Single().PartnerOverrideKey);
    }

    [Fact]
    public void PartnerOverride_MatchesGs02WhenIsa06DoesNot()
    {
        var edi = ClaimLevelDtp472().Replace("*ZZ*SUB001         *", $"*ZZ*{"CLEARINGHOUSE1".PadRight(15)}*", StringComparison.Ordinal);
        var result = Validate(edi, WithLevel2RejectFor("sub001"));
        Assert.Equal("R", result.AcknowledgmentCode);
        Assert.Equal("sub001", result.TransactionSets.Single().PartnerOverrideKey);
    }

    [Fact]
    public void PartnerOverride_UnsetLevelsUseGlobalDefaults()
    {
        // The override only touches Level 2: Level 4 still rejects, Level 3 too.
        var options = WithLevel2RejectFor("SUB001");
        var effective = options.ForSubmitter("SUB001", null, out var key);
        Assert.Equal("SUB001", key);
        Assert.Equal((SnipAction.Reject, SnipAction.Reject, SnipAction.Reject, SnipAction.Reject, SnipAction.Warn),
            (effective.Level1, effective.Level2, effective.Level3, effective.Level4, effective.Level5));

        var unmatched = options.ForSubmitter(OtherSubmitter, OtherSubmitter, out var none);
        Assert.Null(none);
        Assert.Same(options, unmatched);
    }

    [Fact]
    public void PartnerOverride_CanRelaxALevelForOnePartner()
    {
        var options = new Snip837ValidationOptions
        {
            PartnerOverrides = { ["SUB001"] = new SnipLevelOverrides { Level3 = SnipAction.Warn } },
        };
        var unbalanced = Professional(b => b.Replace("CLM*", "CLM*PCN0001*175.00***11:B:1*Y*A*Y*Y"));

        Assert.Equal("E", Validate(unbalanced, options).AcknowledgmentCode);
        Assert.Equal("R", Validate(FromSubmitter(unbalanced, OtherSubmitter), options).AcknowledgmentCode);
    }

    [Fact]
    public void PartnerOverrides_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClaimsImport:Snip:PartnerOverrides:SUB001:Level2"] = "Reject",
        }).Build();

        var options = config.GetSection(Snip837ValidationOptions.SectionName).Get<Snip837ValidationOptions>()!;

        Assert.Equal(SnipAction.Warn, options.Level2);
        Assert.Equal("R", Validate(ClaimLevelDtp472(), options).AcknowledgmentCode);
        Assert.Equal("E", Validate(FromSubmitter(ClaimLevelDtp472(), OtherSubmitter), options).AcknowledgmentCode);
    }

    // ── Import: structured log + durable record ──────────────────────

    [Fact]
    public async Task Import_Level2Warning_IsSubmittedLoggedAndRecordedWithSubmitterAndElement()
    {
        var (controller, logger, transactions, submissions) = Controller(new Snip837ValidationOptions());

        var response = await controller.ImportRaw837(File(ClaimLevelDtp472("CLM-L2W-1")));

        var result = Assert.IsType<Raw837ImportResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal("E", result.AcknowledgmentCode);
        Assert.Equal(1, result.SucceededCount);
        Assert.Contains("IK3*DTP*17*2300*I4~", result.Acknowledgment999);
        await submissions.Received(1).SubmitAsync(Arg.Is<AdapterClaim>(c => c.ClaimNumber == "CLM-L2W-1"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var entry = Assert.Single(logger.Entries, e => e.EventId.Name == "SnipLevel2Warning");
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("L2-2300-DTP472", entry.Properties["RuleId"]);
        Assert.Equal("2300 DTP*472", entry.Properties["Location"]);
        Assert.Equal("SUB001", entry.Properties["SubmitterId"]);
        Assert.Equal("ZZ", entry.Properties["SubmitterQualifier"]);
        Assert.Equal("SUB001", entry.Properties["ApplicationSenderCode"]);
        Assert.Equal("000000101", entry.Properties["InterchangeControlNumber"]);
        Assert.Equal("101", entry.Properties["GroupControlNumber"]);
        Assert.Equal("0001", entry.Properties["TransactionSetControlNumber"]);
        Assert.Equal("CLM-L2W-1", entry.Properties["ClaimNumber"]);
        Assert.Equal(true, entry.Properties["SetAccepted"]);
        Assert.Equal("default", entry.Properties["SnipPolicy"]);

        // No member data in any log line.
        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain("TESTPATIENT", e.Message);
            Assert.DoesNotContain("MEM0001", e.Message);
            Assert.DoesNotContain("19800101", e.Message);
        });

        var record = Assert.Single(transactions.Created);
        Assert.Equal(("Accepted", "E", "ZZ", "SUB001", "SUB001", "000000101", "101"),
            (record.Status, record.AcknowledgmentCode, record.SubmitterQualifier, record.SubmitterId,
             record.ApplicationSenderCode, record.InterchangeControlNumber, record.GroupControlNumber));
        Assert.Null(record.SnipPartnerOverride);
        var warning = Assert.Single(record.SnipWarnings);
        Assert.Equal((2, "L2-2300-DTP472", "Warning", true, "2300", "DTP", 17, "2300 DTP*472", "0001"),
            (warning.Level, warning.RuleId, warning.Severity, warning.ClaimLevel, warning.Loop, warning.SegmentId,
             warning.SegmentPosition, warning.Location, warning.TransactionSetControlNumber));

        // Ops query: who sends a claim-level DTP*472.
        var found = await transactions.ListWithSnipWarningsAsync("tenant-1", ruleId: "L2-2300-DTP472", submitterId: "SUB001");
        Assert.Single(found);
        Assert.Empty(await transactions.ListWithSnipWarningsAsync("tenant-1", ruleId: "L2-2300-DTP472", submitterId: OtherSubmitter));
        Assert.Empty(await transactions.ListWithSnipWarningsAsync("tenant-1", ruleId: "L4-DTP472"));
    }

    [Fact]
    public async Task Import_ElementLevelWarning_RecordsElementLocation()
    {
        var (controller, logger, transactions, _) = Controller(new Snip837ValidationOptions());

        await controller.ImportRaw837(File(Professional(b => b.Replace("CLM*", "CLM*PCN0001*150.00***11:A:1*Y*A*Y*Y"))));

        var entry = Assert.Single(logger.Entries, e => e.EventId.Name == "SnipLevel2Warning");
        Assert.Equal("2300 CLM05-2", entry.Properties["Location"]);
        Assert.Equal("2300 CLM05-2", Assert.Single(transactions.Created).SnipWarnings.Single().Location);
    }

    [Fact]
    public async Task Import_PartnerOverride_RejectsAndIsNotSubmitted_OtherPartnerStillAccepted()
    {
        var options = WithLevel2RejectFor("SUB001");

        var (strict, strictLog, strictTxns, strictSubmissions) = Controller(options);
        var rejected = Assert.IsType<Raw837ImportResult>(Assert.IsType<OkObjectResult>(
            (await strict.ImportRaw837(File(ClaimLevelDtp472("CLM-STRICT")))).Result).Value);
        Assert.Equal("R", rejected.AcknowledgmentCode);
        Assert.Equal(0, rejected.SucceededCount);
        await strictSubmissions.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
        var rejectedRecord = Assert.Single(strictTxns.Created);
        Assert.Equal(("Rejected", "SUB001"), (rejectedRecord.Status, rejectedRecord.SnipPartnerOverride));
        Assert.Contains(rejectedRecord.Errors, e => e.Contains("L2-2300-DTP472"));
        Assert.Empty(rejectedRecord.SnipWarnings);
        Assert.DoesNotContain(strictLog.Entries, e => e.EventId.Name == "SnipLevel2Warning");

        var (lenient, lenientLog, lenientTxns, _) = Controller(options);
        var accepted = Assert.IsType<Raw837ImportResult>(Assert.IsType<OkObjectResult>(
            (await lenient.ImportRaw837(File(FromSubmitter(ClaimLevelDtp472("CLM-LENIENT"), OtherSubmitter)))).Result).Value);
        Assert.Equal("E", accepted.AcknowledgmentCode);
        Assert.Equal(1, accepted.SucceededCount);
        Assert.Equal(OtherSubmitter, Assert.Single(lenientLog.Entries, e => e.EventId.Name == "SnipLevel2Warning").Properties["SubmitterId"]);
        Assert.Equal(OtherSubmitter, Assert.Single(lenientTxns.Created).SubmitterId);
    }

    // ── Harness ──────────────────────────────────────────────────────

    private static IFormFile File(string edi)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(edi);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "l2.837");
    }

    private static (ClaimsV1Controller, CapturingLogger, RecordingRepository, IClaimSubmissionService) Controller(Snip837ValidationOptions options)
    {
        var submissions = Substitute.For<IClaimSubmissionService>();
        submissions.SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));
        var actor = Substitute.For<ICurrentActor>();
        actor.UserId.Returns("ops-test");
        var logger = new CapturingLogger();
        var transactions = new RecordingRepository();

        var controller = new ClaimsV1Controller(
            null!,
            submissions,
            Substitute.For<IExplanationOfBenefitProjector>(),
            transactions,
            new ConfigurationBuilder().Build(),
            actor,
            logger,
            new X12837SnipValidator(options),
            Options.Create(options));
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-1";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, logger, transactions, submissions);
    }

    private sealed class RecordingRepository : IClaimImportTransactionRepository
    {
        private readonly InMemoryClaimImportTransactionRepository _inner = new();
        public ConcurrentQueue<ClaimImportTransaction> Created { get; } = new();

        public Task<ClaimImportTransaction> CreateAsync(ClaimImportTransaction txn)
        {
            Created.Enqueue(txn);
            return _inner.CreateAsync(txn);
        }

        public Task<IReadOnlyList<ClaimImportTransaction>> ListRecentAsync(string tenantId, int limit = 100)
            => _inner.ListRecentAsync(tenantId, limit);

        public Task<IReadOnlyList<ClaimImportTransaction>> ListWithSnipWarningsAsync(
            string tenantId, string? ruleId = null, int? level = null, string? submitterId = null, int limit = 100)
            => _inner.ListWithSnipWarningsAsync(tenantId, ruleId, level, submitterId, limit);
    }

    internal sealed record LogEntry(LogLevel Level, EventId EventId, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingLogger : ILogger<ClaimsV1Controller>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception), properties));
        }
    }
}
