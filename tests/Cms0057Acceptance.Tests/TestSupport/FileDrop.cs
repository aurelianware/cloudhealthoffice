using AuthorizationService.Import;
using ClaimsService.Import.Cpcds;
using ClaimsService.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ChoAuthorization = AuthorizationService.Models.Authorization;
using ChoClaim = ClaimsService.Models.Claim;
using ChoLob = CloudHealthOffice.Infrastructure.Programs.LineOfBusiness;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// The file-drop integration (<c>augment.filedrop</c>): a plan keeps its core
/// and its UM system, and feeds CHO the extracts they already produce. These
/// helpers run the published sample files (docs/integration/samples) through
/// the real importers — <see cref="CpcdsClaimImportService"/> and
/// <see cref="AuthorizationImportService"/> — and hand back the records they
/// wrote, so a scenario test can serve them on the same path its Replace test
/// uses. Only the stores are stand-ins. Synthetic data only.
///
/// What a file-drop test shows: data that arrived as a core's extract reaches
/// the CMS-0057-F surface intact. What it does not show: that a given plan's
/// extract maps cleanly. That is per-plan column mapping, done with dry runs.
/// </summary>
internal static class FileDrop
{
    public const string Backend = "augment.filedrop";

    /// <summary>A sample file shipped beside the test assembly (see the csproj).</summary>
    public static string Sample(string relativePath)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", relativePath));

    /// <summary>
    /// Load a CPCDS claim file through the real importer and return the claim
    /// versions it created. The member crosswalk is the default pass-through,
    /// so a claim's member id is the core's own, as in a real drop.
    /// </summary>
    public static async Task<IReadOnlyList<ChoClaim>> ImportCpcdsClaimsAsync(
        string tenant, string csv, string sourceSystem = "qnxt")
    {
        var created = new List<ChoClaim>();
        var claims = new Mock<IClaimRepository>();
        claims.Setup(r => r.GetByClaimNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((string number) => created.LastOrDefault(c => c.ClaimNumber == number));
        claims.Setup(r => r.CreateAsync(It.IsAny<ChoClaim>()))
            .ReturnsAsync((ChoClaim c) => { created.Add(c); return c; });

        var service = new CpcdsClaimImportService(
            claims.Object,
            new Mock<IClaimImportTransactionRepository>().Object,
            new PassThroughMemberCrosswalk(),
            NullLogger<CpcdsClaimImportService>.Instance);

        var result = await service.ImportAsync(
            tenant, csv,
            new CpcdsImportOptions { SourceSystem = sourceSystem, FileName = "CPCDS_Claims.csv", LineOfBusiness = ChoLob.Medicaid },
            actorId: "file-drop");

        if (result.FileErrors.Count > 0)
            throw new InvalidOperationException("Sample CPCDS file did not load: " + string.Join("; ", result.FileErrors));
        return created;
    }

    /// <summary>
    /// Load a UM platform's authorization CSV through the real importer into
    /// <paramref name="store"/> (a fresh in-memory store when null) and return
    /// what it holds afterwards.
    /// </summary>
    public static async Task<IReadOnlyList<ChoAuthorization>> ImportAuthorizationsAsync(
        string tenant, string csv, InMemoryAuthorizationRepository? store = null, string sourceSystem = "jiva")
    {
        store ??= new InMemoryAuthorizationRepository();
        var service = new AuthorizationImportService(store, NullLogger<AuthorizationImportService>.Instance);

        var result = await service.ImportCsvAsync(
            tenant, csv,
            new AuthorizationImportOptions { SourceSystem = sourceSystem, FileName = "jiva_authorizations.csv" },
            actorId: "file-drop");

        if (result.FileErrors.Count > 0)
            throw new InvalidOperationException("Sample authorization file did not load: " + string.Join("; ", result.FileErrors));
        return (await store.SearchAsync(null, null, null, null, null, null, 1, 1000)).ToList();
    }
}
