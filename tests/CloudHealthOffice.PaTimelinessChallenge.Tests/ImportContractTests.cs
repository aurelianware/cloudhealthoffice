using AuthorizationService.Import;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using AuthorizationService.Services.Cms0057;
using AuthorizationService.Services.Fhir;
using CloudHealthOffice.Tools.PaTimelinessChallenge;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>
/// The corpus files load through CHO's real authorization import and reporting
/// ledger code, in process. This proves the file contract the live run depends
/// on. It does not score CHO: the deployed-stack run does that.
/// </summary>
public sealed class ImportContractTests
{
    private const string Tenant = "pa-timeliness-challenge";
    private static readonly IReadOnlyList<PaCase> History =
        CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.History).ToList();

    [Fact]
    public void Every_column_is_recognized_and_every_case_maps()
    {
        var parsed = AuthorizationCsvReader.Read(PaCorpusWriter.HistoryCsv(History), new AuthorizationImportOptions());

        Assert.Empty(parsed.FileErrors);
        Assert.Empty(parsed.UnrecognizedColumns);
        Assert.Equal(History.Count, parsed.Candidates.Count);
        Assert.All(parsed.Candidates, c =>
        {
            Assert.Empty(c.Errors);
            Assert.NotNull(c.Authorization);
        });
    }

    [Fact]
    public void Status_timing_scope_and_lines_survive_the_import()
    {
        var byNumber = AuthorizationCsvReader.Read(PaCorpusWriter.HistoryCsv(History), new AuthorizationImportOptions())
            .Candidates.ToDictionary(c => c.AuthorizationNumber, c => c.Authorization!);

        foreach (var c in History)
        {
            var a = byNumber[c.AuthorizationNumber];
            Assert.Equal(c.ReceivedAt.UtcDateTime, AuthorizationDeadlinePolicy.Utc(a.SubmittedDate));
            Assert.Equal(c.LineOfBusiness.ToString(), a.LineOfBusiness.ToString());
            Assert.Equal(c.Expected.Expedited, Cms0057Reporting.IsExpedited(a));
            Assert.Equal(c.Expected.InScope, PriorAuthorizationApiScope.IsInScope(a));
            Assert.Equal(c.Lines.Count, a.RequestedServices.Count);
            Assert.Equal(Status(c.Expected.InitialOutcome), Cms0057Reporting.DecisionStatus(a) is var s && Cms0057Reporting.IsDecided(s)
                ? s : AuthorizationStatus.Pended);
            if (c.Decision is { } d)
                Assert.Equal((c.ReceivedAt + d.After).UtcDateTime, AuthorizationDeadlinePolicy.Utc(a.ReviewedDate!.Value));
            var adverse = Cms0057Reporting.AdverseReasons(a);
            Assert.Equal(c.Expected.AdverseReasons.Select(r => (r.LineNumber, r.Code)),
                adverse.Where(r => r.ReviewDecision == "A3").Select(r => (r.LineNumber, r.Code!)));
        }
    }

    [Fact]
    public async Task Import_then_sourced_facts_are_accepted_by_the_reporting_ledger()
    {
        var store = await ImportAsync();

        var facts = PaCorpusWriter.ReportingFacts(History).ToList();
        Assert.Contains(facts, f => f.Kind == "ReviewExtension");
        Assert.Contains(facts, f => f.Kind == "AppealDecision");
        foreach (var f in facts)
        {
            var a = store.Values.Single(x => x.AuthorizationNumber == f.AuthorizationNumber);
            // The real validator: it throws for any fact the endpoint would refuse.
            var fact = PaReportingEvidence.Prepare(a, Command(f), "pa-timeliness-challenge", CorpusFixture.AsOf.UtcDateTime);
            a.ReportingLedger!.Facts.Add(fact);
        }
        Assert.All(store.Values, a => Assert.NotNull(a.ReportingLedger));
    }

    internal static RecordPaReportingFact Command(PaCorpusWriter.ReportingFact f) => new()
    {
        EventId = f.EventId,
        Kind = Enum.Parse<PaReportingFactKind>(f.Kind),
        EffectiveAt = f.EffectiveAt,
        Outcome = f.Outcome is null ? null : Enum.Parse<AuthorizationStatus>(f.Outcome),
        OriginalDueAt = f.OriginalDueAt,
        ExtendedDueAt = f.ExtendedDueAt,
        SourceSystem = f.SourceSystem,
        EvidenceReference = f.EvidenceReference,
    };

    /// <summary>Runs CHO's import service against an in-memory store that initializes records as the Mongo repository does.</summary>
    internal static async Task<Dictionary<string, Authorization>> ImportAsync()
    {
        var store = new Dictionary<string, Authorization>(StringComparer.Ordinal);
        var repository = new Mock<IAuthorizationRepository>();
        repository.Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((string n) => store.Values.FirstOrDefault(a => a.AuthorizationNumber == n));
        repository.Setup(r => r.CreateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) =>
            {
                a.Revision = 0;
                AuthorizationDeadlinePolicy.Initialize(a);
                PaReportingEvidence.Initialize(a, CorpusFixture.AsOf.UtcDateTime);
                store[a.Id] = a;
                return a;
            });
        var service = new AuthorizationImportService(repository.Object, NullLogger<AuthorizationImportService>.Instance);
        var result = await service.ImportCsvAsync(Tenant, PaCorpusWriter.HistoryCsv(History),
            new AuthorizationImportOptions { SourceSystem = PaCorpusGenerator.SourceSystem, FileName = "authorizations.csv" },
            "pa-timeliness-challenge");
        Assert.Empty(result.FileErrors);
        Assert.Equal(History.Count, store.Count);
        return store;
    }

    private static AuthorizationStatus Status(PaOutcome outcome) => outcome switch
    {
        PaOutcome.Approved => AuthorizationStatus.Approved,
        PaOutcome.Modified => AuthorizationStatus.Modified,
        PaOutcome.Denied => AuthorizationStatus.Denied,
        _ => AuthorizationStatus.Pended,
    };
}
